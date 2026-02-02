import { useEffect, useState, useCallback } from "react";
import {
  initializePushNotifications,
  setupMessageHandler,
  getCurrentToken,
  unregisterDeviceToken,
  isFCMSupported,
} from "../services/firebaseService";
import { useAuthStore } from "../store/authStore";
import toast from "react-hot-toast";

interface PushNotificationPayload {
  notification?: {
    title?: string;
    body?: string;
    image?: string;
  };
  data?: {
    notificationId?: string;
    type?: string;
    userId?: string;
    [key: string]: string | undefined;
  };
}

/**
 * Hook để quản lý Firebase Cloud Messaging push notifications
 */
export function useFirebaseMessaging() {
  const { user } = useAuthStore();
  const [token, setToken] = useState<string | null>(null);
  const [isSupported, setIsSupported] = useState(false);
  const [isInitialized, setIsInitialized] = useState(false);

  // Initialize FCM when user logs in
  useEffect(() => {
    if (!user || isInitialized) return;

    const initFCM = async () => {
      try {
        // Check if browser has basic support (synchronous check)
        const basicSupport = isFCMSupported();
        setIsSupported(basicSupport);

        if (!basicSupport) {
          console.log("FCM not supported in this browser (missing browser APIs)");
          return;
        }

        // Initialize and register (this will also check Firebase SDK support)
        const fcmToken = await initializePushNotifications((payload) => {
          // Handle foreground notifications
          handleNotification(payload);
        });

        if (fcmToken) {
          setToken(fcmToken);
          setIsInitialized(true);
          console.log("FCM initialized successfully");
        }
      } catch (error) {
        console.error("Error initializing FCM:", error);
      }
    };

    initFCM();
  }, [user, isInitialized]);

  // Setup message handler
  useEffect(() => {
    if (!isInitialized || !token) return;

    const unsubscribe = setupMessageHandler((payload) => {
      handleNotification(payload);
    });

    return () => {
      if (unsubscribe) unsubscribe();
    };
  }, [isInitialized, token]);

  // Cleanup on logout
  useEffect(() => {
    if (!user && token) {
      const cleanup = async () => {
        try {
          await unregisterDeviceToken(token);
          setToken(null);
          setIsInitialized(false);
        } catch (error) {
          console.error("Error unregistering device:", error);
        }
      };
      cleanup();
    }
  }, [user, token]);

  const handleNotification = useCallback((payload: PushNotificationPayload) => {
    // Show browser notification if permission granted
    if (Notification.permission === "granted") {
      const notification = payload.notification;
      if (notification) {
        const browserNotification = new Notification(notification.title || "AURA", {
          body: notification.body,
          icon: notification.image || "/favicon.ico",
          badge: "/favicon.ico",
          tag: payload.data?.notificationId,
          data: payload.data,
        });

        browserNotification.onclick = () => {
          window.focus();
          // Navigate to relevant page based on notification type
          if (payload.data?.type === "analysis_complete" && payload.data?.analysisId) {
            window.location.href = `/analysis/${payload.data.analysisId}`;
          } else if (payload.data?.type === "high_risk_alert" && payload.data?.analysisId) {
            window.location.href = `/analysis/${payload.data.analysisId}`;
          }
          browserNotification.close();
        };
      }
    }

    // Show toast notification
    if (payload.notification) {
      toast.success(payload.notification.body || payload.notification.title || "New notification", {
        duration: 5000,
      });
    }
  }, []);

  const requestPermission = useCallback(async () => {
    try {
      const permission = await Notification.requestPermission();
      if (permission === "granted") {
        const fcmToken = await getCurrentToken();
        if (fcmToken) {
          setToken(fcmToken);
          toast.success("Push notifications enabled");
        }
      } else {
        toast.error("Notification permission denied");
      }
      return permission === "granted";
    } catch (error) {
      console.error("Error requesting permission:", error);
      toast.error("Failed to enable push notifications");
      return false;
    }
  }, []);

  return {
    token,
    isSupported,
    isInitialized,
    requestPermission,
  };
}
