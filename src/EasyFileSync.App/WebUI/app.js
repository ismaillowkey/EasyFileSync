/* EasyFileSync UI - markup and classes follow GoodSync's real DOM (index.html/styles.css). */

(function () {
  'use strict';

  window.__lastError = null;
  window.addEventListener('error', function (e) {
    window.__lastError = String(e.message) + ' @' + e.filename + ':' + e.lineno;
  });

  var bridge = (window.chrome && window.chrome.webview) ? window.chrome.webview : null;

  var state = {
    jobs: [],
    selected: null,
    rows: [],
    busy: false,
    logOpen: false,
    wide: false,
    modal: null,
    browse: null
  };

  function $(id) { return document.getElementById(id); }

  function send(type, payload) {
    if (!bridge) { console.log('[no bridge]', type, payload); return; }
    bridge.postMessage({ type: type, payload: payload || {} });
  }

  /* ================= message pump ================= */

  function onHostMessage(ev) {
    var msg = typeof ev.data === 'string' ? parse(ev.data) : ev.data;
    if (!msg || !msg.type) return;

    switch (msg.type) {
      case 'jobs': applyJobs(msg.payload); break;
      case 'selected': applySelected(msg.payload); break;
      case 'plan': applyPlan(msg.payload); break;
      case 'summary': case 'status': applySummary(msg.payload); break;
      case 'progress': applyProgress(msg.payload); break;
      case 'log': appendLog(msg.payload); break;
      case 'logClear': $('log_window').innerHTML = ''; break;
      case 'busy': setBusy(!!(msg.payload && msg.payload.busy)); break;
      case 'toast': toast(msg.payload && msg.payload.text); break;
      case 'modalClose': closeModal(); break;
      case 'space': applySpace(msg.payload); break;
      case 'userInfo': $('user_and_comp_info').textContent = (msg.payload && msg.payload.text) || ''; break;
      case 'editor':
        if (msg.isNew) openNewJobDialog(msg.job && msg.job.name);
        else openJobOptions(msg.job, msg.focusSide);
        break;
      case 'localListing': applyLocalListing(msg.payload); break;
      case 'autorun': applyAutorun(msg.payload); break;
      default: break;
    }
  }

  function parse(t) { try { return JSON.parse(t); } catch (e) { return null; } }

  if (bridge) bridge.addEventListener('message', onHostMessage);

  /* ================= left: menu + big buttons + job tree ================= */

  var MENU = [
    { id: 'menu_job', text: 'Job' },
    { id: 'menu_all', text: 'All', isTab: true },
    { id: 'menu_view', text: 'View' },
    { id: 'menu_action', text: 'Action' },
    { id: 'menu_tools', text: 'Tools' },
    { id: 'menu_help', text: 'Help' }
  ];

  function buildMenu() {
    var box = $('main_menu_window');
    box.innerHTML = '';

    MENU.forEach(function (m) {
      var el = document.createElement('button');
      el.id = m.id;
      el.textContent = m.text;

      if (m.isTab) {
        el.className = 'top_menu_tab_active';
      } else {
        el.className = 'top_menu_item';
      }

      el.addEventListener('click', function () {
        if (m.id === 'menu_job') {
          openJobMenu();
        } else if (m.id === 'menu_help') {
          toast('EasyFileSync 1.0 - sync lokal / FTP / SMB');
        } else {
          toast(m.text + ' menu belum diimplementasikan');
        }
      });

      box.appendChild(el);
    });
  }

  function buildLeftButtons() {
    var box = $('left_img_menu');
    box.innerHTML = '';

    // Tombol toolbar sisi kiri (Account Sync OFF dihapus sesuai instruksi)
    var leftDefs = [
      {
        id: 'btn_new_job',
        img: 'svg/newjob.svg',
        text: 'New Job',
        cmd: function () { send('newJob'); }
      },
      {
        id: 'btn_new_group',
        img: 'svg/newgroup.svg',
        text: 'New Group',
        cmd: function () { toast('Group belum diimplementasikan'); }
      },
      {
        id: 'btn_autorun_toggle',
        img: 'svg/autorunon.svg',
        text: 'AutoRun ON',
        cmd: function () { send('toggleAutorun'); }
      }
    ];

    leftDefs.forEach(function (d) {
      var btn = document.createElement('button');
      btn.className = 'gs_toolbar_btn';
      btn.id = d.id;

      var img = document.createElement('img');
      img.src = d.img;
      img.alt = d.text;
      btn.appendChild(img);

      var lbl = document.createElement('span');
      lbl.textContent = d.text;
      btn.appendChild(lbl);

      btn.addEventListener('click', d.cmd);
      box.appendChild(btn);
    });

    // Tombol "More >" di pojok kanan bawah toolbar kiri
    var more = document.createElement('button');
    more.id = 'more_info';
    more.textContent = 'More >';
    more.addEventListener('click', function () {
      state.wide = !state.wide;
      if (state.wide) {
        $('left_window').classList.add('wide');
      } else {
        $('left_window').classList.remove('wide');
      }
    });
    box.appendChild(more);
  }

  function applyJobs(payload) {
    if (payload && payload.jobs) {
      state.jobs = payload.jobs;
    } else {
      state.jobs = [];
    }
    renderJobs();
  }

  function renderJobs() {
    var box = $('left_tree_window');
    box.innerHTML = '';

    // Buat root item "All Jobs" sesuai tampilan GoodSync
    var rootItem = document.createElement('div');
    rootItem.className = 'tree_root_item';

    var rootArrow = document.createElement('span');
    rootArrow.className = 'tree_root_arrow';
    rootArrow.textContent = '\u25be';

    var rootCircle = document.createElement('span');
    rootCircle.className = 'tree_root_circle';

    var rootLabel = document.createElement('span');
    rootLabel.textContent = 'All Jobs';

    rootItem.appendChild(rootArrow);
    rootItem.appendChild(rootCircle);
    rootItem.appendChild(rootLabel);
    box.appendChild(rootItem);

    if (state.jobs.length === 0) {
      var hint = document.createElement('div');
      hint.style.padding = '0.5rem 1.6rem';
      hint.style.color = '#808080';
      hint.style.fontSize = '0.78rem';
      hint.textContent = 'No jobs yet. Click New Job.';
      box.appendChild(hint);
      return;
    }

    state.jobs.forEach(function (job) {
      var row = document.createElement('div');
      row.className = 'tree_job_row';

      var isSelected = false;
      if (state.selected && state.selected.name === job.name) {
        isSelected = true;
      }

      if (isSelected) {
        row.classList.add('selected');
      }

      var iconSpan = document.createElement('span');
      iconSpan.className = 'tree_job_icon';

      // Ikon status di samping nama job
      if (isSelected) {
        iconSpan.classList.add('tree_job_icon_sync');
        iconSpan.textContent = '\u21bb'; // Curved sync arrow
      } else {
        iconSpan.classList.add('tree_job_icon_check');
        iconSpan.textContent = '\u2713'; // Green checkmark
      }
      row.appendChild(iconSpan);

      var nameSpan = document.createElement('span');
      nameSpan.textContent = job.name;
      row.appendChild(nameSpan);

      row.addEventListener('click', function () {
        if (state.busy) {
          return;
        }
        send('selectJob', { name: job.name });
      });

      row.addEventListener('dblclick', function () {
        if (state.busy) {
          return;
        }
        send('openOptions', { name: job.name });
      });

      box.appendChild(row);
    });
  }

  /* ================= right: toolbar buttons ================= */

  function buildRightButtons() {
    var leftBox = $('right_left_img_menu');
    leftBox.innerHTML = '';

    // Tombol aksi tengah: Analyze dan Sync
    var analyzeBtn = makeToolbarBtn('svg/analyze.svg', 'Analyze', function () { send('analyze'); }, 'btn_analyze');
    var syncBtn = makeToolbarBtn('svg/sync.svg', 'Sync', function () { send('sync'); }, 'btn_sync');

    leftBox.appendChild(analyzeBtn);
    leftBox.appendChild(syncBtn);

    var rightBox = $('right_right_img_menu');
    rightBox.innerHTML = '';

    // Tombol aksi kanan: Options dan Auto
    var optionsBtn = makeToolbarBtn('svg/settings.svg', 'Options', function () { send('openOptions'); }, 'btn_options');
    var autoBtn = makeToolbarBtn('svg/autorunoff.svg', 'Auto', function () { toast('Auto belum diimplementasikan'); }, 'btn_auto');

    rightBox.appendChild(optionsBtn);
    rightBox.appendChild(autoBtn);
  }

  function makeToolbarBtn(imgSrc, label, cmd, id) {
    var btn = document.createElement('button');
    btn.className = 'gs_toolbar_btn';
    if (id) {
      btn.id = id;
    }

    var img = document.createElement('img');
    img.src = imgSrc;
    img.alt = label;
    btn.appendChild(img);

    var lbl = document.createElement('span');
    lbl.textContent = label;
    btn.appendChild(lbl);

    btn.addEventListener('click', cmd);
    return btn;
  }

  /* ================= job detail card ================= */

  function applySelected(job) {
    state.selected = job;
    state.rows = [];

    var jobName = '';
    var leftText = 'Please click here to select folder';
    var rightText = 'Please click here to select folder';

    if (job) {
      if (job.name) {
        jobName = job.name;
      }
      if (job.leftPath && job.leftPath.trim().length > 0) {
        leftText = job.leftPath.trim();
      }
      if (job.rightPath && job.rightPath.trim().length > 0) {
        rightText = job.rightPath.trim();
      }
    }

    $('left_url').textContent = leftText;
    $('right_url').textContent = rightText;

    // Perbarui arah panah di antara kedua kotak (2-way atau 1-way)
    var arrowEl = $('center_direction_arrow');
    if (arrowEl) {
      if (job && job.direction === 0) {
        arrowEl.textContent = '\u21c4';
      } else if (job && job.direction === 2) {
        arrowEl.textContent = '\u2190';
      } else {
        arrowEl.textContent = '\u2192';
      }
    }

    // Ubah title window agar sesuai GoodSync: GoodSync - [JobName]
    if (jobName.length > 0) {
      document.title = 'GoodSync - ' + jobName;
    } else {
      document.title = 'GoodSync';
    }

    renderJobs();
    renderRows();
    updateButtons();
  }

  function applySpace(p) {
    if (!p) return;
    if (typeof p.leftPercent === 'number') $('left_diagram_occup1').style.width = p.leftPercent + '%';
    if (typeof p.rightPercent === 'number') $('right_diagram_occup1').style.width = p.rightPercent + '%';
    if (p.leftFreeText) $('left_space').textContent = p.leftFreeText;
    if (p.rightFreeText) $('right_space').textContent = p.rightFreeText;
  }

  /* ================= right table (GoodSync dyn_tab markup) ================= */

  var COLUMNS = [
    { key: 'chk',        title: '',        width: '1.6rem', align: 'center' },
    { key: 'name',       title: 'Item',    width: 'auto',   align: 'left' },
    { key: 'leftSize',   title: 'L Size',  width: '5rem',   align: 'right' },
    { key: 'leftDate',   title: 'L Date',  width: '7rem',   align: 'right' },
    { key: 'leftAction', title: 'L',       width: '1.5rem', align: 'center' },
    { key: 'center',     title: '',        width: '2.5rem', align: 'center' },
    { key: 'rightAction',title: 'R',       width: '1.5rem', align: 'center' },
    { key: 'rightDate',  title: 'R Date',  width: '7rem',   align: 'left' },
    { key: 'rightSize',  title: 'R Size',  width: '5rem',   align: 'left' },
    { key: 'status',     title: 'Status',  width: '8rem',   align: 'left' }
  ];

  function renderRows() {
    var box = $('right_table_window');
    box.innerHTML = '';

    // Jika belum ada data baris (tampilan awal saat job baru dipilih/dibuat)
    if (state.rows.length === 0) {
      var tutorialContainer = document.createElement('div');
      tutorialContainer.className = 'gs_tutorial_container';

      // Step 1: Choose Left and Right Folders
      var step1 = document.createElement('div');
      step1.className = 'gs_tutorial_step';
      step1.style.cursor = 'pointer';

      var s1IconWrap = document.createElement('span');
      s1IconWrap.className = 'gs_tutorial_icon';
      var s1Badge = document.createElement('span');
      s1Badge.className = 'gs_tutorial_icon_folder';
      s1Badge.textContent = '?';
      s1IconWrap.appendChild(s1Badge);

      var s1Text = document.createElement('span');
      s1Text.textContent = '1. Choose the Left and the Right Sync Folders for your Job.';

      step1.appendChild(s1IconWrap);
      step1.appendChild(s1Text);
      step1.addEventListener('click', function () {
        if (!state.busy) {
          send('browseSide', { side: 'left' });
        }
      });

      // Step 2: Apply
      var step2 = document.createElement('div');
      step2.className = 'gs_tutorial_step';

      var s2IconWrap = document.createElement('span');
      s2IconWrap.className = 'gs_tutorial_icon';
      var s2Check = document.createElement('span');
      s2Check.className = 'gs_tutorial_icon_check';
      s2Check.textContent = '\u2713';
      s2IconWrap.appendChild(s2Check);

      var s2Text = document.createElement('span');
      s2Text.textContent = '2. Click Apply to confirm the choice of Sync Folders.';

      step2.appendChild(s2IconWrap);
      step2.appendChild(s2Text);

      // Step 3: Analyze
      var step3 = document.createElement('div');
      step3.className = 'gs_tutorial_step';
      step3.style.cursor = 'pointer';

      var s3IconWrap = document.createElement('span');
      s3IconWrap.className = 'gs_tutorial_icon';
      var s3Search = document.createElement('span');
      s3Search.className = 'gs_tutorial_icon_search';
      s3Search.textContent = '\ud83d\udd0d';
      s3IconWrap.appendChild(s3Search);

      var s3Text = document.createElement('span');
      s3Text.textContent = '3. Click Analyze to see the proposed File Copy and Delete operations.';

      step3.appendChild(s3IconWrap);
      step3.appendChild(s3Text);
      step3.addEventListener('click', function () {
        if (!state.busy && state.selected) {
          send('analyze');
        }
      });

      tutorialContainer.appendChild(step1);
      tutorialContainer.appendChild(step2);
      tutorialContainer.appendChild(step3);
      box.appendChild(tutorialContainer);
      return;
    }

    var head = document.createElement('div');
    head.className = 'dyn_tab_head';
    head.style.display = 'flex';
    head.style.padding = '0 0.25rem';

    COLUMNS.forEach(function (c) {
      var cell = document.createElement('div');
      cell.className = 'dyn_tab_col_head dyn_cell';
      cell.style.width = c.width;
      if (c.width === 'auto') {
        cell.style.flex = '1 1 auto';
      } else {
        cell.style.flex = '0 0 ' + c.width;
      }
      cell.style.textAlign = c.align;
      cell.textContent = c.title;
      head.appendChild(cell);
    });

    box.appendChild(head);

    var body = document.createElement('div');
    body.className = 'dyn_tab_body striped_children';
    body.style.flex = '1 1 auto';
    body.style.overflow = 'auto';

    state.rows.forEach(function (r, index) {
      var row = document.createElement('div');
      row.className = 'dyn_tab_row';
      row.style.display = 'flex';
      row.style.alignItems = 'center';
      row.style.cursor = 'default';
      if (r.rowClass) {
        row.classList.add(r.rowClass);
      }
      if (r.canToggle && r.selected === false) row.style.opacity = '0.45';

      COLUMNS.forEach(function (c) {
        var cell = document.createElement('div');
        cell.className = 'dyn_tab_cell dyn_cell';
        cell.style.width = c.width;
        cell.style.flex = c.width === 'auto' ? '1 1 auto' : '0 0 ' + c.width;
        cell.style.textAlign = c.align;
        cell.style.minWidth = '0';

        if (c.key === 'chk') {
          if (r.canToggle) {
            var cb = document.createElement('input');
            cb.type = 'checkbox';
            cb.checked = r.selected !== false;
            cb.dataset.idx = String(index);
            cb.addEventListener('change', function (ev) {
              send('toggleItem', { index: +ev.target.dataset.idx });
            });
            cell.appendChild(cb);
          }
        } else if (c.key === 'name') {
          var inner = document.createElement('div');
          inner.className = 'dyn_tab_cell_inner';
          inner.style.display = 'flex';
          inner.style.alignItems = 'center';
          inner.style.gap = '0.25rem';
          inner.style.paddingLeft = ((r.depth || 0) * 0.9) + 'rem';

          var ic = document.createElement('img');
          ic.src = r.isFolder ? 'svg/folder.svg' : 'svg/file.svg';
          ic.alt = '';
          ic.style.width = '15px';
          ic.style.height = '15px';
          ic.style.flex = '0 0 auto';
          inner.appendChild(ic);

          var nm = document.createElement('span');
          nm.textContent = r.name;
          nm.style.whiteSpace = 'nowrap';
          nm.style.overflow = 'hidden';
          nm.style.textOverflow = 'ellipsis';
          inner.appendChild(nm);

          cell.appendChild(inner);
        } else if (c.key === 'status') {
          cell.textContent = r.statusText || '';
          cell.style.background = r.statusBg || 'transparent';
        } else {
          cell.textContent = r[c.key] || '';
        }

        row.appendChild(cell);
      });

      body.appendChild(row);
    });

    box.appendChild(body);
  }

  /* ================= summary / progress / busy ================= */

  function applySummary(p) {
    if (!p) return;
    var line = $('right_top_err_line');
    if (line && p.summary) {
      line.textContent = p.summary;
      line.style.display = '';
    }
  }

  function applyProgress(p) {
    if (!p) return;
    var line = $('right_top_err_line');
    if (!line) return;

    var bits = [];
    if (p.items) bits.push(p.items);
    if (p.speed) bits.push(p.speed);
    if (p.eta) bits.push(p.eta);
    if (typeof p.percent === 'number' && p.percent > 0) bits.push(Math.round(p.percent) + '%');
    if (p.currentItem) bits.push(p.currentItem);

    if (bits.length) {
      line.textContent = bits.join('   ');
      line.style.display = '';
    }
  }

  function setBusy(busy) {
    state.busy = busy;
    var stop = $('btn_stop');
    if (stop) stop.disabled = !busy;
    updateButtons();
  }

  function updateButtons() {
    var has = !!state.selected && !state.busy;
    ['btn_analyze', 'btn_sync'].forEach(function (id) {
      var b = $(id);
      if (b) b.disabled = !has;
    });
    var opt = $('btn_options');
    if (opt) opt.disabled = !state.selected;
  }

  function applyAutorun(p) {
    var btn = $('btn_autorun_state');
    toast((p && p.suspended) ? 'AutoRun suspended' : 'AutoRun on');
  }

  /* ================= log ================= */

  function appendLog(line) {
    if (!line) return;
    var box = $('log_window');
    var div = document.createElement('div');
    var lvl = String(line.level || 'info').toLowerCase();
    div.style.fontSize = '0.75rem';
    div.style.whiteSpace = 'pre-wrap';
    div.style.color = lvl === 'error' ? '#c03030' : (lvl === 'debug' ? '#888' : '#202020');
    div.textContent = (line.time ? line.time + '  ' : '') + (line.text || '');
    box.appendChild(div);
    while (box.childElementCount > 800) box.removeChild(box.firstChild);
    if (state.logOpen) box.scrollTop = box.scrollHeight;
  }

  /* ================= toast ================= */

  var toastTimer = null;
  function toast(text) {
    if (!text) return;
    var el = $('html_err');
    if (!el) return;
    el.textContent = text;
    el.style.cssText =
      'display:block;position:fixed;left:50%;bottom:1.2rem;transform:translateX(-50%);' +
      'background:rgba(32,32,32,.92);color:#fff;padding:.4rem .9rem;border-radius:.25rem;' +
      'font-size:.78rem;z-index:9999;pointer-events:none;';
    clearTimeout(toastTimer);
    toastTimer = setTimeout(function () { el.style.display = 'none'; }, 2400);
  }

  /* ================= modal (GoodSync #my_modal) ================= */

  function openModal(title, build, onOk, okLabel, options) {
    var layer = $('my_modal');
    layer.style.display = 'flex';
    layer.style.alignItems = 'center';
    layer.style.justifyContent = 'center';
    layer.innerHTML = '';

    // Siapkan opsi modal
    var modalOptions = {};
    if (options) {
      modalOptions = options;
    }

    var dialogWidth = '26rem';
    if (modalOptions.width) {
      dialogWidth = modalOptions.width;
    }

    var box = document.createElement('div');
    box.className = 'gs_modal_window';
    box.style.width = dialogWidth;

    if (modalOptions.maxWidth) {
      box.style.maxWidth = modalOptions.maxWidth;
    } else {
      box.style.maxWidth = '92vw';
    }

    // Header modal
    var head = document.createElement('div');
    head.className = 'gs_modal_header';

    var titleBox = document.createElement('div');
    titleBox.className = 'gs_modal_title_box';

    // Tambahkan ikon judul jika tersedia
    if (modalOptions.icon) {
      var iconImg = document.createElement('img');
      iconImg.className = 'gs_modal_icon';
      iconImg.src = modalOptions.icon;
      iconImg.alt = '';
      titleBox.appendChild(iconImg);
    }

    var titleText = document.createElement('span');
    titleText.className = 'gs_modal_title';
    titleText.textContent = title;
    titleBox.appendChild(titleText);
    head.appendChild(titleBox);

    // Tombol close [X]
    var closeBtn = document.createElement('button');
    closeBtn.className = 'gs_modal_close_btn';
    closeBtn.type = 'button';
    closeBtn.textContent = '\u2715';
    closeBtn.setAttribute('title', 'Close');
    closeBtn.addEventListener('click', closeModal);
    head.appendChild(closeBtn);

    // Body modal
    var bodyEl = document.createElement('div');
    bodyEl.className = 'gs_modal_body';

    // Footer modal
    var foot = document.createElement('div');
    foot.className = 'gs_modal_footer';

    var cancelBtn = document.createElement('button');
    cancelBtn.type = 'button';
    cancelBtn.className = 'gs_btn gs_btn_secondary';
    cancelBtn.textContent = 'Cancel';
    cancelBtn.addEventListener('click', closeModal);

    var okBtn = document.createElement('button');
    okBtn.type = 'button';
    okBtn.className = 'gs_btn gs_btn_primary';
    if (okLabel) {
      okBtn.textContent = okLabel;
    } else {
      okBtn.textContent = 'OK';
    }

    okBtn.addEventListener('click', function () {
      if (!state.modal) {
        return;
      }
      var isSuccess = state.modal.onOk();
      if (isSuccess !== false) {
        closeModal();
      }
    });

    foot.appendChild(cancelBtn);
    foot.appendChild(okBtn);

    box.appendChild(head);
    box.appendChild(bodyEl);
    box.appendChild(foot);
    layer.appendChild(box);

    state.modal = { onOk: onOk, collect: null };
    build(bodyEl);

    setTimeout(function () {
      var targetInput = bodyEl.querySelector('input, select, textarea');
      if (targetInput) {
        targetInput.focus();
        // Bila input teks biasa, pilih seluruh teks untuk memudahkan pengetikan
        if (targetInput.type === 'text') {
          targetInput.select();
        }
      }
    }, 50);
  }

  function closeModal() {
    var layer = $('my_modal');
    layer.style.display = 'none';
    layer.innerHTML = '';
    state.modal = null;
    state.browse = null;
  }

  document.addEventListener('keydown', function (e) {
    if (!state.modal) {
      return;
    }
    if (e.key === 'Escape') {
      closeModal();
    }
    if (e.key === 'Enter') {
      if (e.target && e.target.tagName !== 'TEXTAREA') {
        var isSuccess = state.modal.onOk();
        if (isSuccess !== false) {
          closeModal();
        }
      }
    }
  });

  /* ================= New Job dialog (GoodSync 90%+ Match) ================= */

  function openNewJobDialog(suggestedName) {
    // Tentukan nama awal untuk job baru
    var initialJobName = 'Job 1';
    if (suggestedName) {
      var cleanName = suggestedName.trim();
      if (cleanName.length > 0) {
        initialJobName = cleanName;
      }
    }

    var dialogOptions = {
      width: '470px',
      icon: 'svg/newjob.svg'
    };

    openModal('New EasyFileSync Job', function (body) {
      body.innerHTML =
        '<div class="nj_section_label">Enter Job Name:</div>' +
        '<input type="text" id="nj_name" class="nj_input_name" maxlength="80" autocomplete="off" spellcheck="false">' +
        '<div class="nj_section_label" style="margin-top: 14px;">Select Job Type:</div>' +
        '<div id="nj_types" class="nj_cards_container"></div>' +
        '<div id="nj_err" class="nj_error_msg"></div>';

      // Daftar tipe sinkronisasi dan backup sesuai screenshot GoodSync
      var jobTypes = [
        {
          directionValue: 1,
          iconPath: 'svg/dirltor.svg',
          title: 'Backup',
          badge: '',
          description: 'One-way Job: Copy files and folders (and their deletions) from the Left side folder to the Right side folder.'
        },
        {
          directionValue: 0,
          iconPath: 'svg/dir2way.svg',
          title: 'Synchronize',
          badge: '',
          description: 'Two-way Job: Propagate changes (file changed, renamed, or deleted) from Left to Right and from Right to Left. Hidden and System files are excluded by default'
        }
      ];

      var cardsContainer = body.querySelector('#nj_types');
      var nameInputField = body.querySelector('#nj_name');
      var errorLabel = body.querySelector('#nj_err');

      nameInputField.value = initialJobName;

      // Render setiap item kartu tipe job
      jobTypes.forEach(function (item, index) {
        var card = document.createElement('div');
        card.className = 'nj_card';

        // Opsi pertama adalah default terpilih
        var isDefaultSelected = false;
        if (index === 0) {
          isDefaultSelected = true;
          card.classList.add('selected');
        }

        var radioInput = document.createElement('input');
        radioInput.type = 'radio';
        radioInput.name = 'nj_direction_choice';
        radioInput.value = String(item.directionValue);
        radioInput.className = 'nj_card_radio';
        radioInput.checked = isDefaultSelected;

        var iconWrap = document.createElement('div');
        iconWrap.className = 'nj_card_icon_wrap';
        var iconImg = document.createElement('img');
        iconImg.src = item.iconPath;
        iconImg.alt = item.title;
        iconWrap.appendChild(iconImg);

        var contentCol = document.createElement('div');
        contentCol.className = 'nj_card_content';

        var titleRow = document.createElement('div');
        titleRow.className = 'nj_card_title_row';

        var titleSpan = document.createElement('span');
        titleSpan.className = 'nj_card_title';
        titleSpan.textContent = item.title;

        var badgeSpan = document.createElement('span');
        badgeSpan.className = 'nj_card_badge';
        badgeSpan.textContent = item.badge;

        titleRow.appendChild(titleSpan);
        titleRow.appendChild(badgeSpan);

        var descPara = document.createElement('div');
        descPara.className = 'nj_card_desc';
        descPara.textContent = item.description;

        contentCol.appendChild(titleRow);
        contentCol.appendChild(descPara);

        card.appendChild(radioInput);
        card.appendChild(iconWrap);
        card.appendChild(contentCol);

        // Saat kartu diklik, aktifkan radio dan beri efek visual terpilih
        card.addEventListener('click', function () {
          var allCards = cardsContainer.querySelectorAll('.nj_card');
          allCards.forEach(function (c) {
            c.classList.remove('selected');
          });

          card.classList.add('selected');
          radioInput.checked = true;
          errorLabel.textContent = '';
        });

        cardsContainer.appendChild(card);
      });

      // Kumpulkan dan validasi data saat user klik OK
      state.modal.collect = function () {
        var enteredName = nameInputField.value.trim();

        if (enteredName.length === 0) {
          errorLabel.textContent = 'Job name cannot be empty.';
          nameInputField.focus();
          return null;
        }

        var checkedRadio = cardsContainer.querySelector('input[name="nj_direction_choice"]:checked');
        var selectedDirection = 0;
        if (checkedRadio) {
          selectedDirection = parseInt(checkedRadio.value, 10);
        }

        return {
          name: enteredName,
          direction: selectedDirection
        };
      };
    }, function () {
      var collectedData = state.modal.collect();
      if (!collectedData) {
        return false;
      }
      send('createJob', collectedData);
      return true;
    }, 'OK', dialogOptions);
  }

  /* ================= Job Options dialog ================= */

  function openJobOptions(d, focusSide) {
    d = d || {};

    openModal('Job Options \u2014 ' + (d.name || ''), function (body) {
      body.innerHTML =
        '<div id="jo_err" style="color:#F0483F;font-size:.75rem;min-height:1rem"></div>' +
        '<div style="display:grid;grid-template-columns:1fr 1fr;gap:1rem">' +
          '<div>' +
            '<div style="color:#1C62D8;font-weight:600;font-size:.8rem;margin-bottom:.3rem">LEFT</div>' +
            '<input type="text" id="jo_lpath" placeholder="Folder path" style="width:100%">' +
            '<div style="margin-top:.35rem"><button class="img_btn_gor" id="jo_lbrowse" type="button">Browse\u2026</button></div>' +
          '</div>' +
          '<div>' +
            '<div style="color:#1C62D8;font-weight:600;font-size:.8rem;margin-bottom:.3rem">RIGHT</div>' +
            '<input type="text" id="jo_rpath" placeholder="Folder path" style="width:100%">' +
            '<div style="margin-top:.35rem"><button class="img_btn_gor" id="jo_rbrowse" type="button">Browse\u2026</button></div>' +
          '</div>' +
        '</div>' +
        '<div style="display:flex;flex-wrap:wrap;gap:.3rem 1rem;margin-top:.4rem">' +
          '<label><input type="checkbox" id="jo_propdel"> Propagate deletions</label>' +
          '<label><input type="checkbox" id="jo_exhidden"> Exclude hidden</label>' +
          '<label><input type="checkbox" id="jo_exsystem"> Exclude system</label>' +
          '<label><input type="checkbox" id="jo_extemp"> Exclude temp files</label>' +
        '</div>' +
        '<div style="display:grid;grid-template-columns:1fr 1fr;gap:1rem;margin-top:.3rem">' +
          '<div><div style="font-size:.72rem;color:#606060">Include patterns</div>' +
            '<textarea id="jo_include" rows="3" style="width:100%"></textarea></div>' +
          '<div><div style="font-size:.72rem;color:#606060">Exclude patterns</div>' +
            '<textarea id="jo_exclude" rows="3" style="width:100%"></textarea></div>' +
        '</div>';

      body.querySelector('#jo_lpath').value = d.leftPath || '';
      body.querySelector('#jo_rpath').value = d.rightPath || '';
      body.querySelector('#jo_propdel').checked = d.propagateDeletes !== false;
      body.querySelector('#jo_exhidden').checked = d.excludeHidden !== false;
      body.querySelector('#jo_exsystem').checked = d.excludeSystem !== false;
      body.querySelector('#jo_extemp').checked = d.excludeTemp !== false;
      body.querySelector('#jo_include').value = d.include || '';
      body.querySelector('#jo_exclude').value = d.exclude || '';

      body.querySelector('#jo_lbrowse').addEventListener('click', function () {
        openFolderBrowser('left', body);
      });
      body.querySelector('#jo_rbrowse').addEventListener('click', function () {
        openFolderBrowser('right', body);
      });

      if (focusSide) {
        setTimeout(function () { openFolderBrowser(focusSide, body); }, 80);
      }

      state.modal.collect = function () {
        return {
          name: d.name,
          leftPath: body.querySelector('#jo_lpath').value.trim(),
          rightPath: body.querySelector('#jo_rpath').value.trim(),
          propagateDeletes: body.querySelector('#jo_propdel').checked,
          excludeHidden: body.querySelector('#jo_exhidden').checked,
          excludeSystem: body.querySelector('#jo_exsystem').checked,
          excludeTemp: body.querySelector('#jo_extemp').checked,
          include: body.querySelector('#jo_include').value,
          exclude: body.querySelector('#jo_exclude').value
        };
      };
    }, function () {
      var data = state.modal.collect();
      if (!data) return false;
      send('saveJobOptions', data);
      return true;
    }, 'Save', { width: '560px', icon: 'svg/settings.svg' });
  }

  /* ================= folder browser ("This Computer") ================= */

  function openFolderBrowser(side, body) {
    if (!state.browse) {
      state.browse = { side: side, path: '', parent: '', selectedPath: '' };
    }

    var prev = body.querySelector('.fb_wrap');
    if (prev) prev.remove();

    var wrap = document.createElement('div');
    wrap.className = 'fb_wrap';
    wrap.style.cssText =
      'display:flex;gap:.6rem;border:1px solid #ddd;border-radius:.25rem;padding:.5rem;' +
      'height:17rem;background:#fff;margin-top:.25rem';

    wrap.innerHTML =
      '<div style="flex:0 0 9rem;border:1px solid #eee;border-radius:.25rem;padding:.3rem;background:#f5f6f7;overflow:auto">' +
        '<div id="fb_root" style="cursor:pointer;padding:.3rem .4rem;border-radius:.25rem">&#128187; This Computer</div>' +
      '</div>' +
      '<div style="flex:1 1 auto;display:flex;flex-direction:column;gap:.35rem;min-width:0">' +
        '<div style="display:flex;gap:.35rem;align-items:center">' +
          '<button class="img_btn_gor" id="fb_up" type="button">Up</button>' +
          '<input type="text" id="fb_path" readonly style="flex:1 1 auto;font-family:monospace;font-size:.72rem;background:#f5f6f7">' +
        '</div>' +
        '<div id="fb_list" style="flex:1 1 auto;overflow:auto;border:1px solid #eee;border-radius:.25rem"></div>' +
        '<div id="fb_sel" style="font-size:.72rem;color:#606060"></div>' +
      '</div>';

    body.appendChild(wrap);

    wrap.querySelector('#fb_root').addEventListener('click', function () { requestListing(''); });
    wrap.querySelector('#fb_up').addEventListener('click', function () {
      var parent = state.browse && state.browse.parent;
      requestListing(parent || '');
    });

    requestListing(state.browse.path || '');
  }


  function requestListing(path) {
    send('listLocal', { path: path || '' });
  }

  function applyLocalListing(p) {
    if (!p || !state.browse) return;

    var list = document.getElementById('fb_list');
    var pathBox = document.getElementById('fb_path');
    var selBox = document.getElementById('fb_sel');
    if (!list) return;

    if (p.error) {
      list.innerHTML = '<div style="padding:.5rem;color:#F0483F;font-size:.78rem"></div>';
      list.firstChild.textContent = p.error;
      return;
    }

    state.browse.path = p.path || '';
    state.browse.parent = p.parent || '';

    list.innerHTML = '';
    pathBox.value = p.path || 'This Computer';

    (p.dirs || []).forEach(function (dir) {
      var row = document.createElement('div');
      row.style.cssText = 'display:flex;align-items:center;gap:.35rem;padding:.25rem .45rem;font-size:.8rem;cursor:pointer;white-space:nowrap;overflow:hidden;text-overflow:ellipsis';

      var img = document.createElement('img');
      img.src = 'svg/folder.svg';
      img.style.width = '15px';
      img.style.height = '15px';
      row.appendChild(img);

      var span = document.createElement('span');
      span.textContent = dir.isDrive ? (dir.label + '  (' + dir.name + ')') : dir.name;
      row.appendChild(span);

      row.addEventListener('mouseenter', function () { row.style.background = '#f0f0f0'; });
      row.addEventListener('mouseleave', function () { if (!row.dataset.sel) row.style.background = ''; });

      row.addEventListener('click', function () {
        var nodes = list.querySelectorAll('div');
        for (var i = 0; i < nodes.length; i++) {
          nodes[i].dataset.sel = '';
          nodes[i].style.background = '';
        }
        row.dataset.sel = '1';
        row.style.background = '#cfe9ff';
        state.browse.selectedPath = dir.full;
        selBox.textContent = 'Selected: ' + dir.full;
      });

      row.addEventListener('dblclick', function () {
        state.browse.selectedPath = dir.full;
        requestListing(dir.full);
      });

      list.appendChild(row);
    });

    if (!(p.dirs || []).length) {
      var empty = document.createElement('div');
      empty.style.cssText = 'padding:.5rem;color:#808080;font-size:.78rem';
      empty.textContent = 'No subfolders (this folder can still be selected)';
      list.appendChild(empty);
    }

    // Selecting the current folder itself is valid.
    state.browse.selectedPath = p.path || '';
    selBox.textContent = p.path ? ('Selected: ' + p.path) : 'Select a folder';

    // Persist choice into the input as soon as the user navigates.
    if (state.browse.side === 'left') {
      var l = document.getElementById('jo_lpath');
      if (l && p.path) l.value = p.path;
    } else {
      var r = document.getElementById('jo_rpath');
      if (r && p.path) r.value = p.path;
    }
  }

  /* ================= menus / misc ================= */

  function openJobMenu() {
    if (!state.selected) { send('newJob'); return; }

    openModal('Job \u2014 ' + state.selected.name, function (body) {
      body.innerHTML =
        '<button class="img_btn_gor" id="jm_opts" style="justify-content:flex-start">Job Options\u2026</button>' +
        '<button class="img_btn_gor" id="jm_del" style="justify-content:flex-start">Delete Job</button>';

      body.querySelector('#jm_opts').addEventListener('click', function () { closeModal(); send('openOptions'); });
      body.querySelector('#jm_del').addEventListener('click', function () { closeModal(); send('deleteJob'); });
    }, function () { return true; }, 'Close');
  }

  $('open_log_window').addEventListener('click', function () {
    state.logOpen = !state.logOpen;
    $('right_bottom_window').style.display = state.logOpen ? 'flex' : 'none';
    if (state.logOpen) {
      var box = $('log_window');
      box.scrollTop = box.scrollHeight;
    }
  });

  $('log_close_X').addEventListener('click', function () {
    state.logOpen = false;
    $('right_bottom_window').style.display = 'none';
  });

  $('log_menu_button').addEventListener('click', function () { toast('Log menu'); });

  $('more_info').addEventListener('click', function () {
    state.wide = !state.wide;
    $('left_window').style.flexBasis = state.wide ? '50%' : '';
  });

  // clicking the endpoint cards opens the folder browser
  $('left_side_details').addEventListener('click', function () { if (!state.busy) send('browseSide', { side: 'left' }); });
  $('right_side_details').addEventListener('click', function () { if (!state.busy) send('browseSide', { side: 'right' }); });

  buildMenu();
  buildLeftButtons();
  buildRightButtons();

  send('ready');
})();
