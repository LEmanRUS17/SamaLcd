'use strict';
(() => {
  const dialog = document.querySelector('#cropDialog'), canvas = document.querySelector('#cropCanvas');
  const zoom = document.querySelector('#cropZoom'), seek = document.querySelector('#cropSeek');
  const applyButton = document.querySelector('#cropApply'), frameStatus = document.querySelector('#cropFrameStatus');
  let image, source, session, duration = 0, cx, cy, side, requestId = 0, seekTimer;
  const time = seconds => {
    const value = Math.max(0, Math.floor(seconds));
    return `${Math.floor(value / 60)}:${String(value % 60).padStart(2, '0')}`;
  };
  const updateTime = seconds => { document.querySelector('#cropTime').textContent = `${time(seconds)} / ${time(duration)}`; };
  const draw = () => {
    if (!image) return;
    side = Math.min(image.naturalWidth, image.naturalHeight) / Number(zoom.value);
    cx = Math.max(side / 2, Math.min(image.naturalWidth - side / 2, cx));
    cy = Math.max(side / 2, Math.min(image.naturalHeight - side / 2, cy));
    const g = canvas.getContext('2d');
    g.clearRect(0, 0, 480, 480);
    g.drawImage(image, cx - side / 2, cy - side / 2, side, side, 0, 0, 480, 480);
    g.fillStyle = 'rgba(0,0,0,.6)';
    g.beginPath(); g.rect(0, 0, 480, 480); g.arc(240, 240, 239, 0, Math.PI * 2); g.fill('evenodd');
    g.strokeStyle = 'white'; g.lineWidth = 2; g.beginPath(); g.arc(240, 240, 239, 0, Math.PI * 2); g.stroke();
  };
  window.openCropEditor = message => {
    clearTimeout(seekTimer);
    const revision = ++requestId;
    source = message.source; session = message.session; duration = Number(message.duration) || 0;
    document.querySelector('#cropTimeline').classList.toggle('hidden', duration <= 0);
    applyButton.disabled = true;
    const next = new Image();
    next.onload = () => {
      if (revision !== requestId) return;
      image = next;
      const c = message.crop, min = Math.min(image.naturalWidth, image.naturalHeight);
      zoom.value = c ? Math.max(1, Math.min(8, 1 / c.size)) : 1;
      side = min / Number(zoom.value);
      cx = c ? c.x * image.naturalWidth + side / 2 : image.naturalWidth / 2;
      cy = c ? c.y * image.naturalHeight + side / 2 : image.naturalHeight / 2;
      seek.max = Math.max(0, duration - .1); seek.value = message.seconds || 0;
      updateTime(Number(seek.value)); frameStatus.textContent = ''; applyButton.disabled = false;
      draw(); if (!dialog.open) dialog.showModal();
    };
    next.onerror = () => { if (revision === requestId) toast(tr('Cannot preview this file'), 'error'); };
    next.src = message.url;
  };
  seek.oninput = () => {
    clearTimeout(seekTimer);
    const revision = ++requestId, seconds = Number(seek.value);
    updateTime(seconds); frameStatus.textContent = tr('Loading frame…'); applyButton.disabled = true;
    seekTimer = setTimeout(() => {
      if (dialog.open) host.send({cmd:'seekCrop',source,session,seconds,requestId:revision});
    }, 180);
  };
  window.updateCropFrame = message => {
    if (!dialog.open || message.session !== session || message.source !== source || message.requestId !== requestId) return;
    const revision = requestId;
    const fail = error => {
      if (revision !== requestId || !dialog.open) return;
      frameStatus.textContent = error || tr('Cannot preview this file'); applyButton.disabled = false;
    };
    if (message.error) { fail(message.error); return; }
    const next = new Image();
    next.onload = () => {
      if (revision !== requestId || !dialog.open) return;
      // Keep the same crop when changing preview frames, even if decoded dimensions vary.
      cx = cx / image.naturalWidth * next.naturalWidth;
      cy = cy / image.naturalHeight * next.naturalHeight;
      image = next; draw(); updateTime(message.seconds); frameStatus.textContent = ''; applyButton.disabled = false;
    };
    next.onerror = () => fail(); next.src = message.url;
  };
  const invalidatePreview = () => { clearTimeout(seekTimer); requestId++; };
  dialog.addEventListener('cancel', invalidatePreview);
  zoom.oninput = draw;
  canvas.addEventListener('wheel', event => {
    event.preventDefault(); zoom.value = Math.max(1, Math.min(8, Number(zoom.value) * Math.exp(-event.deltaY * .002))); draw();
  }, {passive:false});
  canvas.addEventListener('pointerdown', event => {
    canvas.setPointerCapture(event.pointerId);
    let x = event.clientX, y = event.clientY;
    const move = e => {
      const width = canvas.getBoundingClientRect().width;
      cx -= (e.clientX - x) * side / width; cy -= (e.clientY - y) * side / width;
      x = e.clientX; y = e.clientY; draw();
    };
    const end = () => {canvas.removeEventListener('pointermove',move);canvas.removeEventListener('pointerup',end);canvas.removeEventListener('pointercancel',end);};
    canvas.addEventListener('pointermove',move);canvas.addEventListener('pointerup',end);canvas.addEventListener('pointercancel',end);
  });
  document.querySelector('#cropBackground').onclick = () => host.send({cmd:'editCrop'});
  document.querySelector('#cropCancel').onclick = () => {invalidatePreview();dialog.close();};
  const apply = crop => {host.send({cmd:'applyCrop',source,crop});invalidatePreview();dialog.close();};
  applyButton.onclick = () => apply({x:(cx-side/2)/image.naturalWidth,y:(cy-side/2)/image.naturalHeight,size:1/Number(zoom.value)});
  document.querySelector('#cropOriginal').onclick = () => apply(null);
})();
