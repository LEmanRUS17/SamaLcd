'use strict';
(() => {
  const dialog = document.querySelector('#cropDialog'), canvas = document.querySelector('#cropCanvas');
  const zoom = document.querySelector('#cropZoom');
  let image, source, cx, cy, side;
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
    source = message.source;
    image = new Image();
    image.onload = () => {
      const c = message.crop, min = Math.min(image.naturalWidth, image.naturalHeight);
      zoom.value = c ? Math.max(1, Math.min(8, 1 / c.size)) : 1;
      side = min / Number(zoom.value);
      cx = c ? c.x * image.naturalWidth + side / 2 : image.naturalWidth / 2;
      cy = c ? c.y * image.naturalHeight + side / 2 : image.naturalHeight / 2;
      draw(); if (!dialog.open) dialog.showModal();
    };
    image.onerror = () => toast(tr('Cannot preview this file'), 'error');
    image.src = message.url;
  };
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
  document.querySelector('#cropCancel').onclick = () => dialog.close();
  const apply = crop => {host.send({cmd:'applyCrop',source,crop});dialog.close();};
  document.querySelector('#cropApply').onclick = () => apply({x:(cx-side/2)/image.naturalWidth,y:(cy-side/2)/image.naturalHeight,size:1/Number(zoom.value)});
  document.querySelector('#cropOriginal').onclick = () => apply(null);
})();
