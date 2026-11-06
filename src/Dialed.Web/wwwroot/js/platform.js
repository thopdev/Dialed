// Browser implementations behind the platform interfaces in Dialed.Client (#116).
// One function per capability; Dialed.Web/Platform/*.cs are the only callers.

const state = { wakeLock: null, audio: null, passkeyAbort: null };

// ---- Photos ------------------------------------------------------------------------------------

export function capturePhoto(useCamera, maxDimension) {
    return new Promise(resolve => {
        const input = document.createElement('input');
        input.type = 'file';
        input.accept = 'image/*';
        if (useCamera) {
            input.capture = 'environment';
        }
        input.style.display = 'none';
        document.body.appendChild(input);
        const done = value => { input.remove(); resolve(value); };
        input.addEventListener('cancel', () => done(null));
        input.addEventListener('change', async () => {
            const file = input.files?.[0];
            done(file ? await toJpeg(file, maxDimension) : null);
        });
        input.click();
    });
}

async function toJpeg(file, maxDimension) {
    const bitmap = await createImageBitmap(file, { imageOrientation: 'from-image' });
    const scale = Math.min(1, maxDimension / Math.max(bitmap.width, bitmap.height));
    const canvas = document.createElement('canvas');
    canvas.width = Math.round(bitmap.width * scale);
    canvas.height = Math.round(bitmap.height * scale);
    canvas.getContext('2d').drawImage(bitmap, 0, 0, canvas.width, canvas.height);
    bitmap.close?.();
    const blob = await new Promise(r => canvas.toBlob(r, 'image/jpeg', 0.85));
    const name = (file.name || 'photo').replace(/\.[^.]+$/, '') + '.jpg';
    return { base64: await blobToBase64(blob), contentType: 'image/jpeg', fileName: name };
}

async function blobToBase64(blob) {
    const bytes = new Uint8Array(await blob.arrayBuffer());
    let binary = '';
    for (let i = 0; i < bytes.length; i += 0x8000) {
        binary += String.fromCharCode(...bytes.subarray(i, i + 0x8000));
    }
    return btoa(binary);
}

function base64ToBytes(base64) {
    return Uint8Array.from(atob(base64), c => c.charCodeAt(0));
}

// ---- Files (origin private file system) --------------------------------------------------------

async function directoryFor(path, create) {
    const parts = path.split('/').filter(p => p.length > 0);
    if (parts.some(p => p === '..' || p === '.')) {
        throw new Error('Invalid path');
    }
    let dir = await navigator.storage.getDirectory();
    for (const part of parts.slice(0, -1)) {
        dir = await dir.getDirectoryHandle(part, { create });
    }
    return { dir, name: parts[parts.length - 1] };
}

export async function fileSave(path, base64) {
    const { dir, name } = await directoryFor(path, true);
    const handle = await dir.getFileHandle(name, { create: true });
    // createWritable is missing in older Safari; fall back to a sync access handle (workers only) is not possible here.
    const writable = await handle.createWritable();
    await writable.write(base64ToBytes(base64));
    await writable.close();
}

export async function fileRead(path) {
    try {
        const { dir, name } = await directoryFor(path, false);
        const file = await (await dir.getFileHandle(name)).getFile();
        return await blobToBase64(file);
    } catch (e) {
        if (e.name === 'NotFoundError' || e.name === 'TypeMismatchError') return null;
        throw e;
    }
}

export async function fileExists(path) {
    return (await fileRead(path)) !== null;
}

export async function fileDelete(path) {
    try {
        const { dir, name } = await directoryFor(path, false);
        await dir.removeEntry(name);
    } catch (e) {
        if (e.name !== 'NotFoundError') throw e;
    }
}

export async function fileList(folder) {
    const result = [];
    async function walk(dir, prefix) {
        for await (const [name, handle] of dir.entries()) {
            if (handle.kind === 'directory') await walk(handle, `${prefix}${name}/`);
            else result.push(`${prefix}${name}`);
        }
    }
    try {
        let dir = await navigator.storage.getDirectory();
        const parts = folder.split('/').filter(p => p.length > 0);
        for (const part of parts) dir = await dir.getDirectoryHandle(part);
        await walk(dir, parts.length ? `${parts.join('/')}/` : '');
    } catch (e) {
        if (e.name !== 'NotFoundError') throw e;
    }
    return result;
}

// ---- Connectivity and lifecycle ------------------------------------------------------------------

export function watchConnectivity(dotNetRef) {
    window.addEventListener('online', () => dotNetRef.invokeMethodAsync('OnChanged', true));
    window.addEventListener('offline', () => dotNetRef.invokeMethodAsync('OnChanged', false));
    return navigator.onLine;
}

export function watchVisibility(dotNetRef) {
    document.addEventListener('visibilitychange', () =>
        dotNetRef.invokeMethodAsync('OnVisibilityChanged', document.visibilityState === 'visible'));
    return document.visibilityState === 'visible';
}

// ---- Share ----------------------------------------------------------------------------------------

export function canShare() {
    return typeof navigator.share === 'function';
}

export async function share(title, text, url) {
    if (!canShare()) return false;
    try {
        await navigator.share({ title: title ?? undefined, text: text ?? undefined, url: url ?? undefined });
        return true;
    } catch (e) {
        if (e.name === 'AbortError') return false; // dismissed
        throw e;
    }
}

// ---- Passkeys (same conversion as the spike, ADR-0010) --------------------------------------------

const b64urlToBuf = s => Uint8Array.from(atob(s.replace(/-/g, '+').replace(/_/g, '/').padEnd(Math.ceil(s.length / 4) * 4, '=')), c => c.charCodeAt(0)).buffer;
const bufToB64url = b => btoa(String.fromCharCode(...new Uint8Array(b))).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');

const creationOptions = o => ({
    ...o,
    challenge: b64urlToBuf(o.challenge),
    user: { ...o.user, id: b64urlToBuf(o.user.id) },
    excludeCredentials: (o.excludeCredentials ?? []).map(c => ({ ...c, id: b64urlToBuf(c.id) })),
});

const requestOptions = o => ({
    ...o,
    challenge: b64urlToBuf(o.challenge),
    allowCredentials: (o.allowCredentials ?? []).map(c => ({ ...c, id: b64urlToBuf(c.id) })),
});

const credentialJson = c => ({
    id: c.id,
    rawId: bufToB64url(c.rawId),
    type: c.type,
    authenticatorAttachment: c.authenticatorAttachment ?? undefined,
    clientExtensionResults: c.getClientExtensionResults(),
    response: c.response.attestationObject
        ? {
            attestationObject: bufToB64url(c.response.attestationObject),
            clientDataJSON: bufToB64url(c.response.clientDataJSON),
            transports: c.response.getTransports?.() ?? [],
        }
        : {
            authenticatorData: bufToB64url(c.response.authenticatorData),
            clientDataJSON: bufToB64url(c.response.clientDataJSON),
            signature: bufToB64url(c.response.signature),
            userHandle: c.response.userHandle ? bufToB64url(c.response.userHandle) : null,
        },
});

export function passkeysSupported() {
    return typeof window.PublicKeyCredential === 'function' && !!navigator.credentials;
}

export async function passkeysAutofillSupported() {
    return passkeysSupported() && (await PublicKeyCredential.isConditionalMediationAvailable?.()) === true;
}

function outcome(status, responseJson, error) {
    return { status, responseJson: responseJson ?? null, error: error ?? null };
}

async function runPasskey(call) {
    if (!passkeysSupported()) return outcome('unsupported');
    try {
        const credential = await call();
        return credential ? outcome('success', JSON.stringify(credentialJson(credential))) : outcome('cancelled');
    } catch (e) {
        if (e.name === 'AbortError' || e.name === 'NotAllowedError') return outcome('cancelled', null, e.name);
        return outcome('failed', null, `${e.name}: ${e.message}`);
    }
}

export function passkeyCancel() {
    state.passkeyAbort?.abort();
    state.passkeyAbort = null;
}

export function passkeyCreate(optionsJson) {
    passkeyCancel();
    return runPasskey(() => navigator.credentials.create({ publicKey: creationOptions(JSON.parse(optionsJson)) }));
}

export function passkeyGet(optionsJson, autofill) {
    passkeyCancel();
    const request = { publicKey: requestOptions(JSON.parse(optionsJson)) };
    if (autofill) {
        state.passkeyAbort = new AbortController();
        request.mediation = 'conditional';
        request.signal = state.passkeyAbort.signal;
    }
    return runPasskey(() => navigator.credentials.get(request));
}

// ---- Storage persistence ---------------------------------------------------------------------------

export async function storagePersisted() {
    return (await navigator.storage?.persisted?.()) === true;
}

export async function storagePersist() {
    return (await navigator.storage?.persist?.()) === true;
}

export async function storageEstimate() {
    const e = await navigator.storage?.estimate?.();
    return e ? { usage: e.usage ?? 0, quota: e.quota ?? 0 } : null;
}

// ---- Wake lock ---------------------------------------------------------------------------------------

export async function wakeLockAcquire() {
    if (!('wakeLock' in navigator)) return false;
    try {
        state.wakeLock = await navigator.wakeLock.request('screen');
        state.wakeLock.addEventListener('release', () => { state.wakeLock = null; });
        return true;
    } catch {
        return false;
    }
}

export async function wakeLockRelease() {
    await state.wakeLock?.release();
    state.wakeLock = null;
}

// ---- Sound cues (generated, no audio files) ---------------------------------------------------------

export async function playCue(cue) {
    const Context = window.AudioContext ?? window.webkitAudioContext;
    if (!Context) return;
    state.audio ??= new Context();
    if (state.audio.state === 'suspended') await state.audio.resume();
    const beeps = cue === 'done' ? [[880, 0, 0.18], [880, 0.28, 0.18], [1175, 0.56, 0.35]] : [[1000, 0, 0.06]];
    const now = state.audio.currentTime;
    for (const [frequency, offset, length] of beeps) {
        const oscillator = state.audio.createOscillator();
        const gain = state.audio.createGain();
        oscillator.frequency.value = frequency;
        gain.gain.setValueAtTime(0.0001, now + offset);
        gain.gain.exponentialRampToValueAtTime(0.25, now + offset + 0.01);
        gain.gain.exponentialRampToValueAtTime(0.0001, now + offset + length);
        oscillator.connect(gain).connect(state.audio.destination);
        oscillator.start(now + offset);
        oscillator.stop(now + offset + length + 0.02);
    }
}
