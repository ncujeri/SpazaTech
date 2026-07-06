// Local notifications for low stock. Permission is requested lazily on first use.
window.spazaNotify = async function (title, body) {
  if (!("Notification" in window)) {
    return;
  }

  if (Notification.permission === "default") {
    await Notification.requestPermission();
  }

  if (Notification.permission === "granted") {
    const registration = await navigator.serviceWorker?.getRegistration();
    if (registration) {
      await registration.showNotification(title, { body, icon: "icon-192.png" });
    } else {
      new Notification(title, { body, icon: "icon-192.png" });
    }
  }
};
