using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using YoutubeExplode;
using YoutubeExplode.Videos.Streams;
using Xabe.FFmpeg;
using Microsoft.AspNetCore.SignalR;
using System.IO;
using System.Threading.Tasks;

namespace YouTubeToMp3ConverterApp.Pages
{
    public class IndexModel : PageModel
    {
        private readonly IHubContext<ProgressHub> _progressHubContext;
        private readonly ILogger<IndexModel> _logger;

        public IndexModel(IHubContext<ProgressHub> progressHubContext, ILogger<IndexModel> logger)
        {
            _progressHubContext = progressHubContext;
            _logger = logger;

            // Set FFmpeg path
            string ffmpegPath = @"C:\CustomPath"; // Adjust to your FFmpeg path
            if (Directory.Exists(ffmpegPath))
            {
                FFmpeg.SetExecutablesPath(ffmpegPath);
                _logger.LogInformation("FFmpeg path set to: {Path}", ffmpegPath);
            }
            else
            {
                _logger.LogError("FFmpeg path does not exist: {Path}", ffmpegPath);
            }
        }

        [BindProperty]
        public string YouTubeUrl { get; set; }

        public string Message { get; set; }

        public async Task<IActionResult> OnPostAsync()
        {
            if (string.IsNullOrEmpty(YouTubeUrl))
            {
                Message = "Please enter a valid YouTube URL.";
                _logger.LogWarning("YouTube URL is empty.");
                return Page();
            }

            // Generate a unique group ID for this request
            string groupId = Guid.NewGuid().ToString();
            HttpContext.Session.SetString("SignalRGroupId", groupId);
            _logger.LogInformation("Generated SignalR group ID: {GroupId}", groupId);

            try
            {
                _logger.LogInformation("Starting conversion for URL: {Url}", YouTubeUrl);

                var youtube = new YoutubeClient();
                var video = await youtube.Videos.GetAsync(YouTubeUrl);
                _logger.LogInformation("Video title: {Title}, ID: {Id}", video.Title, video.Id);

                var streamManifest = await youtube.Videos.Streams.GetManifestAsync(video.Id);
                var streamInfo = streamManifest.GetAudioOnlyStreams().FirstOrDefault(s => s.Container == Container.Mp4);
                if (streamInfo == null)
                {
                    Message = "No MP4 audio stream available for this video.";
                    _logger.LogWarning("No MP4 audio stream found for video ID: {Id}", video.Id);
                    return Page();
                }
                _logger.LogInformation("Selected stream: Container={Container}, Bitrate={Bitrate}kbps",
                    streamInfo.Container.Name, streamInfo.Bitrate.BitsPerSecond / 1000.0);

                var downloadsPath = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "downloads");
                Directory.CreateDirectory(downloadsPath);

                // Sanitize file name and use .m4a for temp file
                var safeFileName = string.Join("_", video.Title.Split(Path.GetInvalidFileNameChars()));
                var tempFilePath = Path.Combine(downloadsPath, $"{Guid.NewGuid()}.m4a");
                var mp3FilePath = Path.Combine(downloadsPath, $"{safeFileName}.mp3");

                // Download the audio stream with retry
                async Task DownloadWithRetry(int maxRetries = 3)
                {
                    for (int attempt = 1; attempt <= maxRetries; attempt++)
                    {
                        try
                        {
                            _logger.LogInformation("Download attempt {Attempt} for {Path}", attempt, tempFilePath);
                            var progressHandler = new Progress<double>(async p =>
                            {
                                var progress = (int)(p * 100);
                                _logger.LogDebug("Sending progress: {Progress}% at {Time}", progress, DateTime.Now);
                                try
                                {
                                    await _progressHubContext.Clients.Group(groupId)
                                        .SendAsync("ReceiveProgress", progress);
                                    _logger.LogDebug("Progress sent successfully: {Progress}%", progress);
                                }
                                catch (Exception ex)
                                {
                                    _logger.LogError(ex, "Failed to send progress: {Progress}%", progress);
                                }
                            });

                            await youtube.Videos.Streams.DownloadAsync(streamInfo, tempFilePath, progressHandler);
                            _logger.LogInformation("Download completed: {Path}", tempFilePath);
                            return;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Download attempt {Attempt} failed for {Path}", attempt, tempFilePath);
                            if (attempt == maxRetries) throw;
                            await Task.Delay(1000 * attempt); // Exponential backoff
                        }
                    }
                }

                await DownloadWithRetry();

                // Validate temporary file
                if (!System.IO.File.Exists(tempFilePath))
                {
                    Message = "Temporary file was not created.";
                    _logger.LogError("Temporary file missing: {Path}", tempFilePath);
                    return Page();
                }

                var fileInfo = new FileInfo(tempFilePath);
                if (fileInfo.Length == 0)
                {
                    Message = "Temporary file is empty.";
                    _logger.LogError("Temporary file is empty: {Path}", tempFilePath);
                    return Page();
                }
                _logger.LogInformation("Temporary file created: {Path}, Size: {Size} bytes", tempFilePath, fileInfo.Length);

                // Convert to MP3 using FFmpeg
                _logger.LogInformation("Converting to MP3: {OutputPath}", mp3FilePath);
                try
                {
                    var conversion = await FFmpeg.Conversions.FromSnippet.Convert(tempFilePath, mp3FilePath);
                    await conversion.Start();
                    _logger.LogInformation("Conversion completed: {OutputPath}", mp3FilePath);
                }
                catch (Exception ex)
                {
                    Message = $"FFmpeg conversion failed: {ex.Message}";
                    _logger.LogError(ex, "FFmpeg conversion failed for file: {Path}", tempFilePath);
                    return Page();
                }

                // Clean up temporary file
                if (System.IO.File.Exists(tempFilePath))
                {
                    System.IO.File.Delete(tempFilePath);
                    _logger.LogInformation("Deleted temporary file: {Path}", tempFilePath);
                }

                // Stream the MP3 file directly
                if (!System.IO.File.Exists(mp3FilePath))
                {
                    Message = "MP3 file was not created.";
                    _logger.LogError("MP3 file missing: {Path}", mp3FilePath);
                    return Page();
                }

                var fileBytes = await System.IO.File.ReadAllBytesAsync(mp3FilePath);
                _logger.LogInformation("Serving MP3 file: {Path}", mp3FilePath);

                // Clean up MP3 file after streaming
                try
                {
                    System.IO.File.Delete(mp3FilePath);
                    _logger.LogInformation("Deleted MP3 file after streaming: {Path}", mp3FilePath);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to delete MP3 file: {Path}", mp3FilePath);
                }

                return File(fileBytes, "audio/mpeg", $"{safeFileName}.mp3");
            }
            catch (Exception ex)
            {
                Message = $"An error occurred: {ex.Message}";
                _logger.LogError(ex, "Conversion failed for URL: {Url}", YouTubeUrl);
                return Page();
            }
        }

        public IActionResult OnGetGetGroupId()
        {
            var groupId = HttpContext.Session.GetString("SignalRGroupId") ?? Guid.NewGuid().ToString();
            HttpContext.Session.SetString("SignalRGroupId", groupId);
            return Content(groupId);
        }

        public async Task<IActionResult> OnGetTestProgress()
        {
            try
            {
                var groupId = HttpContext.Session.GetString("SignalRGroupId") ?? Guid.NewGuid().ToString();
                HttpContext.Session.SetString("SignalRGroupId", groupId);
                await _progressHubContext.Clients.Group(groupId).SendAsync("ReceiveProgress", 50);
                Message = "Sent test progress: 50%";
                _logger.LogInformation("Sent test progress: 50% to group {GroupId}", groupId);
            }
            catch (Exception ex)
            {
                Message = $"Test failed: {ex.Message}";
                _logger.LogError(ex, "Test progress failed");
            }
            return Page();
        }
    }
}