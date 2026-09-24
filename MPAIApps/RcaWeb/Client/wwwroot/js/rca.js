// THE BROWSER'S PHYSICAL LAYER. What the desktop RCA does with WASAPI, a webcam
// library and WebView2, the browser does itself: the microphone and camera
// through getUserMedia, sound through Web Audio, the avatar in its own page.
window.rca = (() => {
  let ctx = null;        // one AudioContext, opened by the Start click
  let mic = null;        // the microphone stream, asked for once
  let capture = null;    // the capture in progress, so it can be abandoned
  let hear = null;       // where the running microphone sends each block, while listening

  // THE MICROPHONE RUNS FROM START ON, keeping the last second heard. A person
  // who answers the moment the avatar stops speaks before listening has begun;
  // with that second kept, the first word ("yes") is not lost.
  const RING_MS = 1000;
  const ring = [];

  const START = 0.015, QUIET = 0.012, PAUSE_MS = 900, MAX_MS = 20000, PREROLL_MS = 1000;

  function level(x) {
    let sum = 0;
    for (let i = 0; i < x.length; i++) sum += x[i] * x[i];
    return Math.sqrt(sum / x.length);
  }

  function trim(list, ms) {
    let total = list.reduce((a, c) => a + c.length, 0);
    while (list.length > 1 && total - list[0].length >= ctx.sampleRate * ms / 1000) {
      total -= list[0].length;
      list.shift();
    }
  }

  // THE CLICK THAT OPENS THE WAY: sound may play and the microphone may open
  // only after the person has done something.
  async function unlock() {
    ctx = ctx || new (window.AudioContext || window.webkitAudioContext)();
    if (ctx.state === 'suspended') await ctx.resume();
    if (!mic) {
      mic = await navigator.mediaDevices.getUserMedia(
        { audio: { channelCount: 1, echoCancellation: true, noiseSuppression: true, autoGainControl: true } });
      const source = ctx.createMediaStreamSource(mic);
      const node = ctx.createScriptProcessor(4096, 1, 1);
      node.onaudioprocess = e => {
        const block = new Float32Array(e.inputBuffer.getChannelData(0));
        ring.push(block);
        trim(ring, RING_MS);
        if (hear) hear(block);
      };
      source.connect(node);
      node.connect(ctx.destination);   // a processor runs only when connected; its output is silence
    }
  }

  // ONE SPOKEN TURN. Begins with the second just heard, waits for speech if none
  // has started, keeps a moment from before it so the first syllable is not lost,
  // and ends after a pause. Returned as 16 kHz, 16-bit mono PCM, base64 - what
  // Speech Object Acquisition produces.
  function captureSpeech() {
    return new Promise(async resolve => {
      try { await unlock(); } catch (e) { resolve(null); return; }
      const rate = ctx.sampleRate;
      const before = ring.slice(), kept = [];
      let speaking = before.some(b => level(b) > START), quietMs = 0, spokenMs = 0, done = false;
      if (speaking) kept.push(...before);

      function finish(keep) {
        if (done) return;
        done = true;
        hear = null;
        capture = null;
        resolve(keep && speaking ? toPcm16k(kept, rate) : null);
      }
      capture = { abandon: () => finish(false) };

      hear = block => {
        const rms = level(block), ms = block.length * 1000 / rate;
        if (!speaking) {
          before.push(block);
          trim(before, PREROLL_MS);
          if (rms > START) { speaking = true; kept.push(...before); }
          return;
        }
        kept.push(block);
        spokenMs += ms;
        quietMs = rms < QUIET ? quietMs + ms : 0;
        if (quietMs >= PAUSE_MS || spokenMs >= MAX_MS) finish(true);
      };
    });
  }

  function abandonCapture() { if (capture) capture.abandon(); }

  function toPcm16k(chunks, rate) {
    const n = chunks.reduce((a, c) => a + c.length, 0);
    const all = new Float32Array(n);
    let o = 0;
    for (const c of chunks) { all.set(c, o); o += c.length; }
    const ratio = rate / 16000, m = Math.floor(n / ratio);
    const out = new Int16Array(m);
    for (let i = 0; i < m; i++) {                 // average over each step: a plain low-pass
      const a = Math.floor(i * ratio), b = Math.min(n, Math.floor((i + 1) * ratio));
      let s = 0;
      for (let j = a; j < b; j++) s += all[j];
      const v = b > a ? s / (b - a) : all[a];
      out[i] = Math.max(-1, Math.min(1, v)) * 32767;
    }
    return base64(new Uint8Array(out.buffer));
  }

  function base64(bytes) {
    let bin = '';
    for (let i = 0; i < bytes.length; i += 0x8000)
      bin += String.fromCharCode.apply(null, bytes.subarray(i, i + 0x8000));
    return btoa(bin);
  }

  // A FACE, FROM THE CAMERA: one frame, as JPEG, base64.
  async function captureFrame() {
    try {
      const s = await navigator.mediaDevices.getUserMedia({ video: true });
      const v = document.createElement('video');
      v.srcObject = s; v.muted = true; v.playsInline = true;
      await v.play();
      await new Promise(r => setTimeout(r, 400));   // let exposure settle
      const c = document.createElement('canvas');
      c.width = v.videoWidth; c.height = v.videoHeight;
      c.getContext('2d').drawImage(v, 0, 0);
      s.getTracks().forEach(t => t.stop());
      const url = c.toDataURL('image/jpeg', 0.9);
      return url.substring(url.indexOf(',') + 1);
    } catch (e) { console.error(e); return null; }
  }

  // THE AVATAR: the same message the desktop sends it through WebView2.
  function present(faceDescriptorsJson, speechWavBase64) {
    const frame = document.getElementById('avatar');
    if (!frame || !frame.contentWindow) return;
    frame.contentWindow.postMessage(
      { Kind: 'render', FaceDescriptors: faceDescriptorsJson || null, SpeechWavBase64: speechWavBase64 || '' },
      window.location.origin);
  }

  function focus(id) { const e = document.getElementById(id); if (e) e.focus(); }

  // PRESENT WHILE OPEN, GONE WHEN CLOSED. The Service counts this client while it
  // hears from it: a request every 30 seconds keeps it counted, and closing or
  // reloading the page says goodbye - with keepalive, so the request outlives the page.
  let presenceTimer = null;
  function presence(clientId) {
    const headers = { 'MPAI-Client': clientId };
    if (presenceTimer) clearInterval(presenceTimer);
    presenceTimer = setInterval(() => fetch('MPAI/AIFU/Status', { headers }).catch(() => {}), 30000);
    window.addEventListener('pagehide', () => {
      try { fetch('MPAI/AIFU/Leave', { method: 'POST', headers, keepalive: true }); } catch (e) {}
    });
  }

  return { unlock, captureSpeech, abandonCapture, captureFrame, present, focus, presence };
})();
