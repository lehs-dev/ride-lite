const bookTripButton = document.getElementById("bookTripButton");
const tripMessage = document.getElementById("tripMessage");

bookTripButton.addEventListener("click", function () {
    const pickup = document.getElementById("pickupAddress").value.trim();
    const destination = document.getElementById("destinationAddress").value.trim();

    if (pickup === "" || destination === "") {
        tripMessage.textContent = "Bạn cần nhập điểm đón và điểm đến.";
        return;
    }

    tripMessage.textContent =
        "Điểm đón: " + pickup + " | Điểm đến: " + destination;
});

// thêm bản đồ
const map = L.map("rideMap").setView([10.7769, 106.7009], 13);

L.tileLayer("https://tile.openstreetmap.org/{z}/{x}/{y}.png", {
    maxZoom: 19,
    attribution:
        '&copy; <a href="https://www.openstreetmap.org/copyright">OpenStreetMap</a> contributors'
}).addTo(map);


// tòa độ điểm đón
let pickupPoint = null;
//marker trên bản đồ
let pickupMarker = null;
// điểm đến
let destinationPoint = null;
let destinationMarker = null;
let selectedLocation = "pickup";
map.on("click", function (event) {
    if (selectedLocation === "destination") {
        destinationPoint = event.latlng;

        if (destinationMarker !== null) {
            map.removeLayer(destinationMarker);
        }

        destinationMarker = L.marker(destinationPoint)
            .addTo(map)
            .bindPopup("Điểm đến của bạn")
            .openPopup();

        document.getElementById("destinationAddress").value =
            destinationPoint.lat.toFixed(6) + ", " +
            destinationPoint.lng.toFixed(6);

        return;
    }
    pickupPoint = event.latlng;

    if (pickupMarker !== null) {
        map.removeLayer(pickupMarker);
    }

    pickupMarker = L.marker(pickupPoint)
        .addTo(map)
        .bindPopup("Điểm đón của bạn")
        .openPopup();

    document.getElementById("pickupAddress").value =
        pickupPoint.lat.toFixed(6) + ", " + pickupPoint.lng.toFixed(6);
});
document.getElementById("pickupAddress").addEventListener("focus", function () {
    selectedLocation = "pickup";
    tripMessage.textContent = "Bấm vào bản đồ để chọn điểm đón.";
});

document.getElementById("destinationAddress").addEventListener("focus", function () {
    selectedLocation = "destination";
    tripMessage.textContent = "Bấm vào bản đồ để chọn điểm đến.";
});