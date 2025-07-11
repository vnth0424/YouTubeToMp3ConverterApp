const connection = new signalR.HubConnectionBuilder()
    .withUrl("/progressHub")
    .withAutomaticReconnect()
    .build();

connection.on("ReceiveProgress", function (progress) {
    console.log("Received progress: " + progress + "%");
    const progressBar = document.getElementById("progressBar");
    if (progressBar) {
        progressBar.style.width = progress + "%";
        progressBar.setAttribute("aria-valuenow", progress);
        progressBar.innerText = progress + "%";
    } else {
        console.error("Progress bar element not found");
    }
});

connection.start()
    .then(function () {
        console.log("SignalR connection established");
        fetch("/Index?handler=GetGroupId")
            .then(response => {
                if (!response.ok) throw new Error("Failed to fetch groupId: " + response.status);
                return response.text();
            })
            .then(groupId => {
                console.log("Fetched groupId: " + groupId);
                connection.invoke("AddToGroup", groupId)
                    .then(() => console.log("Joined group: " + groupId))
                    .catch(err => console.error("AddToGroup error: " + err.toString()));
            })
            .catch(err => console.error("Fetch groupId error: " + err.toString()));
    })
    .catch(function (err) {
        console.error("SignalR connection error: " + err.toString());
    });