document.querySelectorAll("[data-copy]").forEach((button) => {
  button.addEventListener("click", async () => {
    const sample = button.closest(".code-sample");
    const status = sample.querySelector(".copy-status");
    try {
      await navigator.clipboard.writeText(
        sample.querySelector("code").textContent,
      );
      button.textContent = "Copied ✓";
      status.textContent = "Code copied to clipboard.";
    } catch {
      status.textContent =
        "Copy unavailable. Select the code to copy it manually.";
    }
    setTimeout(() => {
      button.textContent = "Copy ⧉";
    }, 2000);
  });
});
