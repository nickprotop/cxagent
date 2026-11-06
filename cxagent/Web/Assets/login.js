// THE LOGIN PAGE'S ONE JOB BEYOND THE FORM: say why the last attempt failed. The server redirects back with
// ?login=failed or ?login=wait; text only, never HTML.
const reason = new URLSearchParams(location.search).get("login");
const box = document.getElementById("login-error");
if (reason === "failed") { box.textContent = "That user name and password did not match."; box.hidden = false; }
if (reason === "wait") { box.textContent = "Too many attempts. Wait a few seconds and try again."; box.hidden = false; }
