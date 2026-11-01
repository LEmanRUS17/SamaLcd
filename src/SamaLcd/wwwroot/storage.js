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

(() => {
  const dialog = document.querySelector('#sdClearDialog');
  const clear = document.querySelector('#clearSdFiles'), refresh = document.querySelector('#refreshSdFiles');
  let card = null, confirmation = null;
  const total = files => files.reduce((sum, file) => sum + file.size, 0);
  const fillList = (element, files, deletable = false) => element.replaceChildren(...files.map(file => {
    const li = document.createElement('li'), name = document.createElement('span'), size = document.createElement('span');
    name.className = 'sd-file-name'; size.className = 'sd-file-size';
    name.textContent = file.name; size.textContent = `${(file.size / 1048576).toFixed(2)} MiB`;
    li.append(name, size);
    if (deletable) {
      const button = document.createElement('button');
      button.className = 'btn btn-xs btn-error btn-ghost sd-delete-file';
      button.textContent = tr('Delete'); button.disabled = clear.disabled;
      button.setAttribute('aria-label', tr('Delete {0}', file.name));
      button.onclick = () => confirm([file], file.name);
      li.append(button);
    }
    return li;
  }));
  window.renderSdCard = (next, pendingCleanup) => {
    card = next;
    if (confirmation && (!card || card.busy || card.revision !== confirmation.revision || pendingCleanup)) {
      confirmation = null; dialog.close();
    }
    const files = card?.files || [];
    refresh.disabled = !!card?.busy || !!pendingCleanup;
    clear.disabled = refresh.disabled || !card?.revision || !files.length;
    document.querySelector('#sdFilesSummary').textContent = card?.busy ? tr('Reading or updating SD files…') : !card?.files ? tr('Refresh to load the SD file list.') : files.length ? tr('{0} files · {1} MiB', files.length, (total(files) / 1048576).toFixed(2)) : tr('No videos on the SD card.');
    document.querySelector('#sdFilesError').textContent = card?.error || '';
    fillList(document.querySelector('#sdFilesList'), files, true);
  };
  refresh.onclick = () => host.send({cmd:'refreshSdFiles'});
  const confirm = (files, name = null) => {
    if (clear.disabled || !card?.files?.length) return;
    confirmation = {revision:card.revision, files, name};
    fillList(document.querySelector('#sdClearFiles'), confirmation.files);
    document.querySelector('#sdClearSize').textContent = tr('{0} files · {1} MiB', confirmation.files.length, (total(confirmation.files) / 1048576).toFixed(2));
    dialog.showModal(); document.querySelector('#sdClearCancel').focus();
  };
  clear.onclick = () => confirm(card.files);
  const cancel = () => { confirmation = null; dialog.close(); };
  document.querySelector('#sdClearCancel').onclick = cancel;
  dialog.addEventListener('cancel', event => {event.preventDefault(); cancel();});
  document.querySelector('#sdClearApprove').onclick = () => {
    if (!confirmation) return;
    const {revision, name} = confirmation;
    confirmation = null; dialog.close(); clear.disabled = true;
    window.renderSdCard({...card,busy:true});
    host.send(name == null ? {cmd:'clearSdFiles',revision,approved:true} : {cmd:'deleteSdFile',revision,name,approved:true});
  };
})();
