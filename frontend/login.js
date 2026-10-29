const loginButton = document.getElementById("loginButton");
const message = document.getElementById("message");

loginButton.addEventListener("click", async function () {
    const username = document.getElementById("username").value.trim();
    const password = document.getElementById("password").value;

    if (username === "" || password === "") {
        message.textContent = "Bạn cần nhập tên đăng nhập và mật khẩu.";
        return;
    }

    message.textContent = "Đang đăng nhập...";

    try {
        const response = await fetch("http://localhost:5000/auth/login", {
            method: "POST",
            headers: {
                "Content-Type": "application/json"
            },
            body: JSON.stringify({
                username: username,
                password: password
            })
        });

        if (!response.ok) {
            message.textContent = "Đăng nhập thất bại. Mã lỗi: " + response.status;
            return;
        }

        const result = await response.json();
        message.textContent = "Xin chào " + result.user.fullName;
    } catch (error) {
        message.textContent = "Chưa kết nối được API.";
        console.error(error);
    }
});