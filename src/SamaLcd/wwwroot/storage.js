'use strict';
(() => {
  const dialog = document.querySelector('#sdCleanupDialog');
  let pending = null;
  const handled = new Set();
  window.showSdCleanup = request => {
    if (!request) {
      if (pending) handled.add(pending.id);
      pending=null;
      if (dialog.open) dialog.close();
      return;
    }
    if (handled.has(request.id)) return;
    pending = request;
    document.querySelector('#sdCleanupSize').textContent = tr(
      'Required (including reserve): {0} MiB; free: {1} MiB; listed files: {2} MiB.',
      (request.requiredBytes/1048576).toFixed(2), (request.freeBytes/1048576).toFixed(2),
      (request.files.reduce((sum,file)=>sum+file.size,0)/1048576).toFixed(2));
    const list = document.querySelector('#sdCleanupFiles');
    list.replaceChildren(...request.files.map(file => {
      const li=document.createElement('li');
      li.textContent=`${file.name} — ${(file.size/1048576).toFixed(2)} MiB`;
      return li;
    }));
    if (!dialog.open) dialog.showModal();
    document.querySelector('#sdCleanupCancel').focus();
  };
  const decide = approved => {
    if (!pending) return;
    handled.add(pending.id);
    host.send({cmd:'sdCleanupDecision',id:pending.id,approved});
    pending=null;
    dialog.close();
  };
  document.querySelector('#sdCleanupCancel').onclick=()=>decide(false);
  document.querySelector('#sdCleanupApprove').onclick=()=>decide(true);
  dialog.addEventListener('cancel',event=>{event.preventDefault();decide(false);});
})();
