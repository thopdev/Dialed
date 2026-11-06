// Browser storage checks for the #37 spike.

export async function persist() {
  return navigator.storage?.persist ? await navigator.storage.persist() : false;
}

export async function status() {
  const persisted = navigator.storage?.persisted ? await navigator.storage.persisted() : false;
  const estimate = navigator.storage?.estimate ? await navigator.storage.estimate() : {};
  return {
    persisted,
    usageBytes: estimate.usage ?? 0,
    quotaBytes: estimate.quota ?? 0,
    locksSupported: !!navigator.locks,
    standalone: window.matchMedia('(display-mode: standalone)').matches || navigator.standalone === true,
    userAgent: navigator.userAgent,
  };
}

// Holds the lock for 10 seconds so a second tab can try (and should fail) meanwhile.
export async function tryLock(name) {
  if (!navigator.locks) return false;
  return await navigator.locks.request(name, { ifAvailable: true }, async lock => {
    if (lock === null) return false;
    await new Promise(resolve => setTimeout(resolve, 10000));
    return true;
  });
}
