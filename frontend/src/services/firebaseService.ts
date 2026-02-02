import { initializeApp, getApps, FirebaseApp } from "firebase/app";
import {
  getMessaging,
  getToken,
  onMessage,
  Messaging,
  isSupported,
} from "firebase/messaging";
import api from "./api";

// Firebase configuration - Load from environment variables or config
// These should match your Firebase project settings
const firebaseConfig = {
  apiKey: import.meta.env.VITE_FIREBASE_API_KEY || "AIzaSyDWfqRLSBpKD7D6V-_Fdijasd0DH-qbyA0",
  authDomain: import.meta.env.VITE_FIREBASE_AUTH_DOMAIN || "aura-retinal-screening.firebaseapp.com",
  projectId: import.meta.env.VITE_FIREBASE_PROJECT_ID || "aura-retinal-screening",
  storageBucket: import.meta.env.VITE_FIREBASE_STORAGE_BUCKET || "aura-retinal-screening.firebasestorage.app",
  messagingSenderId: import.meta.env.VITE_FIREBASE_SENDER_ID || "422595241352",
  appId: import.meta.env.VITE_FIREBASE_APP_ID || "1:422595241352:web:4d4874be8ad1d21f41863d",
  measurementId: import.meta.env.VITE_FIREBASE_MEASUREMENT_ID || "G-M5XY0P0D3F",
};

// VAPID key for web push (get from Firebase Console > Project Settings > Cloud Messaging > Web Push certificates)
const vapidKey =
  import.meta.env.VITE_FIREBASE_VAPID_KEY ||
  "BQ_JRe4jSOwOCzYHnvwh54102XBBLQC5rDYfHrOWIDMI63hsc1y06DRI3t3A-6wDcltlgDEKOqMmvbX3G79mFWE";

let app: FirebaseApp | null = null;
let messaging: Messaging | null = null;
let messagingSupported = false;

/**
 * Initialize Firebase App
 */
export function initializeFirebase(): FirebaseApp | null {
  if (app) return app;

  try {
    const apps = getApps();
    if (apps.length > 0) {
      app = apps[0];
    } else {
      app = initializeApp(firebaseConfig);
    }
    return app;
  } catch (error) {
    console.error("Firebase initialization error:", error);
    return null;
  }
}

/**
 * Initialize Firebase Cloud Messaging
 */
export async function initializeMessaging(): Promise<Messaging | null> {
  if (messaging) return messaging;

  try {
    // Check if browser supports FCM (Firebase SDK check)
    try {
      messagingSupported = await isSupported();
    } catch (supportError) {
      console.warn("Error checking FCM support:", supportError);
      messagingSupported = false;
    }
    
    if (!messagingSupported) {
      console.warn("Firebase Cloud Messaging is not supported in this browser");
      console.warn("Possible reasons:");
      console.warn("  - Browser không hỗ trợ Service Worker");
      console.warn("  - Browser không hỗ trợ Notification API");
      console.warn("  - Đang chạy trên HTTP (cần HTTPS hoặc localhost)");
      console.warn("  - Browser extension block Service Worker");
      return null;
    }

    if (!app) {
      app = initializeFirebase();
      if (!app) {
        console.error("Failed to initialize Firebase app");
        return null;
      }
    }

    messaging = getMessaging(app);
    console.log("✓ Firebase Messaging initialized successfully");
    return messaging;
  } catch (error) {
    console.error("FCM initialization error:", error);
    console.error("Error details:", error instanceof Error ? error.message : String(error));
    messagingSupported = false;
    return null;
  }
}

/**
 * Đăng ký Service Worker cho FCM và chờ active (bắt buộc trước khi getToken)
 */
async function getServiceWorkerRegistration(): Promise<ServiceWorkerRegistration | null> {
  if (!("serviceWorker" in navigator)) return null;
  try {
    const registration = await navigator.serviceWorker.register(
      "/firebase-messaging-sw.js",
      { scope: "/" }
    );
    await navigator.serviceWorker.ready;
    return registration;
  } catch {
    return null;
  }
}

/**
 * Request notification permission and get FCM token
 */
export async function requestNotificationPermission(): Promise<string | null> {
  try {
    if (!messaging) {
      messaging = await initializeMessaging();
      if (!messaging) return null;
    }

    const permission = await Notification.requestPermission();
    if (permission !== "granted") {
      return null;
    }

    const registration = await getServiceWorkerRegistration();
    if (!registration) {
      return null;
    }

    const token = await getToken(messaging, {
      vapidKey,
      serviceWorkerRegistration: registration,
    });
    if (!token) return null;

    return token;
  } catch {
    return null;
  }
}

/**
 * Register device token with backend
 */
export async function registerDeviceToken(
  deviceToken: string,
  platform: string = "web"
): Promise<boolean> {
  try {
    const response = await api.post("/push-notifications/register-device", {
      deviceToken,
      platform,
      deviceInfo: navigator.userAgent,
    });
    return response.status === 200;
  } catch (error) {
    console.error("Error registering device token:", error);
    return false;
  }
}

/**
 * Unregister device token from backend
 */
export async function unregisterDeviceToken(
  deviceToken: string
): Promise<boolean> {
  try {
    const response = await api.post("/push-notifications/unregister-device", {
      deviceToken,
    });
    return response.status === 200;
  } catch (error) {
    console.error("Error unregistering device token:", error);
    return false;
  }
}

/**
 * Setup FCM message handler (for foreground notifications)
 */
export function setupMessageHandler(
  onMessageReceived: (payload: any) => void
): (() => void) | null {
  if (!messaging) {
    console.warn("Messaging not initialized");
    return null;
  }

  try {
    const unsubscribe = onMessage(messaging, (payload) => {
      console.log("Message received:", payload);
      onMessageReceived(payload);
    });

    return unsubscribe;
  } catch (error) {
    console.error("Error setting up message handler:", error);
    return null;
  }
}

/**
 * Initialize Firebase and register device for push notifications
 */
export async function initializePushNotifications(
  onMessageReceived?: (payload: any) => void
): Promise<string | null> {
  try {
    // Initialize Firebase first
    const firebaseApp = initializeFirebase();
    if (!firebaseApp) {
      console.error("Failed to initialize Firebase app");
      return null;
    }

    // Initialize Messaging (this will check Firebase SDK support)
    await initializeMessaging();
    if (!messaging) {
      console.warn("FCM not supported or failed to initialize. Check browser console for details.");
      return null;
    }

    // Request permission and get token
    const token = await requestNotificationPermission();
    if (!token) {
      return null;
    }

    // Register with backend
    const registered = await registerDeviceToken(token, "web");
    if (!registered) {
      console.warn("Failed to register device token with backend");
    }

    // Setup message handler for foreground notifications
    if (onMessageReceived) {
      setupMessageHandler(onMessageReceived);
    }

    return token;
  } catch (error) {
    console.error("Error initializing push notifications:", error);
    return null;
  }
}

/**
 * Check if FCM is supported (synchronous check)
 * Note: This checks browser capabilities, not Firebase initialization status
 */
export function isFCMSupported(): boolean {
  // Check if browser supports required APIs
  if (typeof window === 'undefined') return false;
  
  // Check for Service Worker support
  if (!('serviceWorker' in navigator)) return false;
  
  // Check for Notification API support
  if (!('Notification' in window)) return false;
  
  // Check for Push Manager support
  if (!('PushManager' in window)) return false;
  
  return true;
}

/**
 * Check if FCM is supported (async, more accurate)
 * This actually checks Firebase SDK support
 */
export async function checkFCMSupportAsync(): Promise<boolean> {
  try {
    const supported = await isSupported();
    messagingSupported = supported;
    return supported;
  } catch (error) {
    console.error("Error checking FCM support:", error);
    messagingSupported = false;
    return false;
  }
}

/**
 * Get current FCM token (if already initialized)
 */
export async function getCurrentToken(): Promise<string | null> {
  if (!messaging) {
    messaging = await initializeMessaging();
    if (!messaging) return null;
  }

  try {
    const registration = await getServiceWorkerRegistration();
    if (!registration) return null;
    return await getToken(messaging, {
      vapidKey,
      serviceWorkerRegistration: registration,
    });
  } catch {
    return null;
  }
}
