// Firebase Cloud Messaging Service Worker
// This file handles background push notifications when the app is not in focus

importScripts('https://www.gstatic.com/firebasejs/10.7.1/firebase-app-compat.js');
importScripts('https://www.gstatic.com/firebasejs/10.7.1/firebase-messaging-compat.js');

// Initialize Firebase (use same config as frontend)
// Note: Service Worker không thể access import.meta.env, nên cần hardcode config
// Update this config to match your Firebase project settings
const firebaseConfig = {
  apiKey: "AIzaSyDWfqRLSBpKD7D6V-_Fdijasd0DH-qbyA0",
  authDomain: "aura-retinal-screening.firebaseapp.com",
  projectId: "aura-retinal-screening",
  storageBucket: "aura-retinal-screening.firebasestorage.app",
  messagingSenderId: "422595241352",
  appId: "1:422595241352:web:4d4874be8ad1d21f41863d",
  measurementId: "G-M5XY0P0D3F",
};

firebase.initializeApp(firebaseConfig);

const messaging = firebase.messaging();

// Handle background messages
messaging.onBackgroundMessage((payload) => {
  console.log('[firebase-messaging-sw.js] Received background message ', payload);
  
  const notificationTitle = payload.notification?.title || 'AURA Notification';
  const notificationOptions = {
    body: payload.notification?.body || '',
    icon: payload.notification?.image || '/favicon.ico',
    badge: '/favicon.ico',
    tag: payload.data?.notificationId,
    data: payload.data,
  };

  self.registration.showNotification(notificationTitle, notificationOptions);
});

// Handle notification click
self.addEventListener('notificationclick', (event) => {
  console.log('[firebase-messaging-sw.js] Notification click received.');
  
  event.notification.close();

  // Open app and navigate to relevant page
  const data = event.notification.data;
  let url = '/';
  
  if (data?.type === 'analysis_complete' && data?.analysisId) {
    url = `/analysis/${data.analysisId}`;
  } else if (data?.type === 'high_risk_alert' && data?.analysisId) {
    url = `/analysis/${data.analysisId}`;
  }

  event.waitUntil(
    clients.matchAll({ type: 'window', includeUncontrolled: true }).then((clientList) => {
      for (const client of clientList) {
        if (client.url === url && 'focus' in client) {
          return client.focus();
        }
      }
      if (clients.openWindow) {
        return clients.openWindow(url);
      }
    })
  );
});
