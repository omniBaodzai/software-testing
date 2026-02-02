/**
 * Utility để check browser support cho FCM
 * Chạy trong browser console để debug
 */

export function checkBrowserSupport() {
  console.log('========================================');
  console.log('🔍 Browser Support Check for FCM');
  console.log('========================================');
  console.log('');

  const checks: Array<{ name: string; supported: boolean; note?: string }> = [];

  // Check Service Worker
  const hasServiceWorker = 'serviceWorker' in navigator;
  checks.push({
    name: 'Service Worker',
    supported: hasServiceWorker,
    note: hasServiceWorker ? undefined : 'Required for FCM background notifications'
  });

  // Check Notification API
  const hasNotification = 'Notification' in window;
  checks.push({
    name: 'Notification API',
    supported: hasNotification,
    note: hasNotification ? undefined : 'Required for browser notifications'
  });

  // Check Push Manager
  const hasPushManager = 'PushManager' in window;
  checks.push({
    name: 'Push Manager',
    supported: hasPushManager,
    note: hasPushManager ? undefined : 'Required for push notifications'
  });

  // Check HTTPS or localhost
  const isSecure = window.location.protocol === 'https:' || 
                   window.location.hostname === 'localhost' ||
                   window.location.hostname === '127.0.0.1';
  checks.push({
    name: 'Secure Context (HTTPS/localhost)',
    supported: isSecure,
    note: isSecure ? undefined : 'Service Workers require HTTPS or localhost'
  });

  // Check IndexedDB (Firebase uses it)
  const hasIndexedDB = 'indexedDB' in window;
  checks.push({
    name: 'IndexedDB',
    supported: hasIndexedDB,
    note: hasIndexedDB ? undefined : 'Firebase uses IndexedDB for caching'
  });

  // Display results
  let allSupported = true;
  checks.forEach(check => {
    const icon = check.supported ? '✓' : '❌';
    const color = check.supported ? 'color: green' : 'color: red';
    console.log(`%c${icon} ${check.name}: ${check.supported ? 'Supported' : 'Not Supported'}`, color);
    if (check.note) {
      console.warn(`  ⚠ ${check.note}`);
    }
    if (!check.supported) allSupported = false;
  });

  console.log('');
  if (allSupported) {
    console.log('%c✓ All browser APIs are supported!', 'color: green; font-weight: bold');
    console.log('If FCM still fails, check Firebase configuration.');
  } else {
    console.log('%c❌ Some browser APIs are missing', 'color: red; font-weight: bold');
    console.log('FCM will not work in this browser/environment.');
  }

  console.log('');
  console.log('Current URL:', window.location.href);
  console.log('Protocol:', window.location.protocol);
  console.log('Hostname:', window.location.hostname);
  console.log('========================================');

  return allSupported;
}

// Expose to window
if (typeof window !== 'undefined') {
  (window as any).checkBrowserSupport = checkBrowserSupport;
}
