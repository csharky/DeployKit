const cloud = window.Telegram?.WebApp?.CloudStorage;

export function storageGet(keys) {
  return new Promise(resolve => {
    if (cloud) {
      cloud.getItems(keys, (err, values) => resolve(err ? {} : values));
    } else {
      const result = {};
      keys.forEach(k => result[k] = localStorage.getItem(k) || '');
      resolve(result);
    }
  });
}

export function storageSet(key, value) {
  if (cloud) cloud.setItem(key, value);
  localStorage.setItem(key, value);
}
