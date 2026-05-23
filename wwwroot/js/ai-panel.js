// ai-panel.js — injectable AI side panel
// Usage: <script src="/js/ai-panel.js"></script>
// API:   AiPanel.open(contextFn?)   — open panel
//        AiPanel.close()            — close panel
//        AiPanel.toggle(contextFn?) — toggle
//        AiPanel.setContext(fn)     — register context provider

(function () {
  'use strict'

  // ── State ──────────────────────────────────────────────────────────────────

  var PANEL_WIDTH_DEFAULT = 420
  var PANEL_WIDTH_MIN     = 260
  var PANEL_WIDTH_MAX     = 800
  var STORAGE_KEY         = 'ai_panel_w'

  var _open        = false
  var _panelW      = parseInt(localStorage.getItem(STORAGE_KEY) || PANEL_WIDTH_DEFAULT)
  var _chatId      = _mkId()
  var _history     = []       // [{role, content}]
  var _streaming   = false
  var _contextFns  = []       // registered context providers
  var _sseSource   = null
  var _selectedModel = localStorage.getItem('ai_selected_model') || 'kr/claude-sonnet-4.5'
  var _availableModels = []

  function _mkId() {
    return 'chat-' + Math.random().toString(36).slice(2)
  }

  // ── DOM ────────────────────────────────────────────────────────────────────

  var _panel, _body, _input, _sendBtn, _newBtn, _interruptBtn, _modelSelect, _resizer, _styleEl

  function _buildDOM() {
    // style injection
    _styleEl = document.createElement('style')
    _styleEl.textContent = `
      #ai-panel-host {
        position: fixed;
        top: 0; right: 0; bottom: 0;
        width: 0;
        z-index: 9000;
        display: flex;
        pointer-events: none;
        transition: none;
      }
      #ai-panel-host.open {
        width: var(--ai-panel-w, 420px);
        pointer-events: all;
      }
      #ai-panel-resizer {
        width: 4px;
        cursor: col-resize;
        background: transparent;
        flex-shrink: 0;
        transition: background 0.15s;
        align-self: stretch;
      }
      #ai-panel-resizer:hover { background: var(--accent, #58a6ff); opacity: 0.6; }
      #ai-panel {
        flex: 1;
        background: var(--bg1, #161b22);
        border-left: 1px solid var(--border, #30363d);
        display: flex;
        flex-direction: column;
        overflow: hidden;
        height: 100%;
      }
      #ai-panel-header {
        display: flex;
        align-items: center;
        gap: 8px;
        padding: 8px 12px;
        border-bottom: 1px solid var(--border, #30363d);
        flex-shrink: 0;
        background: var(--bg1, #161b22);
      }
      #ai-panel-header h3 {
        font-size: 12px;
        font-weight: 700;
        color: var(--accent, #58a6ff);
        margin: 0;
        flex: 1;
      }
      .ai-hbtn {
        background: none;
        border: 1px solid var(--border, #30363d);
        border-radius: 3px;
        color: var(--text2, #8b949e);
        cursor: pointer;
        font-size: 10px;
        padding: 2px 7px;
        line-height: 1.4;
      }
      .ai-hbtn:hover { color: var(--text, #e6edf3); border-color: var(--text2, #8b949e); }
      .ai-hbtn.danger:hover { color: var(--red, #f85149); border-color: var(--red, #f85149); }
      .ai-hbtn:disabled { opacity: 0.5; cursor: not-allowed; }
      #ai-model-select {
        background: var(--bg, #0d1117);
        border: 1px solid var(--border, #30363d);
        border-radius: 3px;
        color: var(--text, #e6edf3);
        font-size: 10px;
        padding: 2px 6px;
        cursor: pointer;
        max-width: 150px;
      }
      #ai-model-select:hover {
        border-color: var(--accent, #58a6ff);
      }
      #ai-panel-msgs {
        flex: 1;
        overflow-y: auto;
        padding: 10px 12px;
        display: flex;
        flex-direction: column;
        gap: 10px;
      }
      .ai-msg {
        display: flex;
        flex-direction: column;
        gap: 3px;
        max-width: 100%;
      }
      .ai-msg-role {
        font-size: 9px;
        font-weight: 700;
        text-transform: uppercase;
        letter-spacing: 0.4px;
        color: var(--text2, #8b949e);
      }
      .ai-msg.user .ai-msg-role { color: var(--accent, #58a6ff); }
      .ai-msg.assistant .ai-msg-role { color: var(--green, #3fb950); }
      .ai-msg-body {
        font-size: 11px;
        line-height: 1.6;
        color: var(--text, #e6edf3);
        white-space: pre-wrap;
        word-break: break-word;
      }
      .ai-msg.user .ai-msg-body {
        background: var(--bg2, #21262d);
        border-radius: 4px;
        padding: 6px 10px;
      }
      .ai-tool-call {
        font-size: 10px;
        font-family: Consolas, monospace;
        color: var(--yellow, #d29922);
        background: var(--bg, #0d1117);
        border: 1px solid var(--border, #30363d);
        border-radius: 4px;
        padding: 4px 8px;
        margin-top: 3px;
        white-space: pre-wrap;
        word-break: break-all;
      }
      #ai-panel-footer {
        border-top: 1px solid var(--border, #30363d);
        padding: 8px 12px;
        display: flex;
        flex-direction: column;
        gap: 6px;
        flex-shrink: 0;
        background: var(--bg1, #161b22);
      }
      #ai-panel-input {
        width: 100%;
        background: var(--bg, #0d1117);
        border: 1px solid var(--border, #30363d);
        border-radius: 4px;
        color: var(--text, #e6edf3);
        font-size: 11px;
        font-family: inherit;
        padding: 7px 10px;
        resize: none;
        outline: none;
        min-height: 60px;
        max-height: 200px;
        box-sizing: border-box;
        line-height: 1.5;
      }
      #ai-panel-input:focus { border-color: var(--accent, #58a6ff); }
      #ai-panel-input:disabled { opacity: 0.5; }
      .ai-footer-row {
        display: flex;
        gap: 6px;
        align-items: center;
        justify-content: flex-end;
      }
      #ai-send-btn {
        background: var(--accent, #58a6ff);
        color: #fff;
        border: none;
        border-radius: 3px;
        padding: 4px 14px;
        font-size: 11px;
        cursor: pointer;
        font-weight: 600;
      }
      #ai-send-btn:hover { opacity: 0.85; }
      #ai-send-btn:disabled { opacity: 0.5; cursor: default; }
      .ai-stream-cursor {
        display: inline-block;
        width: 7px;
        height: 12px;
        background: var(--accent, #58a6ff);
        vertical-align: text-bottom;
        animation: ai-blink 0.8s steps(1) infinite;
      }
      @keyframes ai-blink { 0%,100%{opacity:1} 50%{opacity:0} }
      .ai-empty {
        color: var(--text2, #8b949e);
        font-size: 11px;
        text-align: center;
        padding: 30px 10px;
        line-height: 1.6;
      }
      /* body shift */
      body.ai-panel-open {
        margin-right: var(--ai-panel-w, 420px);
        transition: margin-right 0.2s ease;
      }
    `
    document.head.appendChild(_styleEl)

    // host
    var host = document.createElement('div')
    host.id  = 'ai-panel-host'
    host.innerHTML = `
      <div id="ai-panel-resizer"></div>
      <div id="ai-panel">
        <div id="ai-panel-header">
          <h3>⟡ AI Agent</h3>
          <select id="ai-model-select" title="Select AI Model"></select>
          <button class="ai-hbtn" id="ai-interrupt-btn" title="Interrupt agent (Ctrl+C)" disabled>⏸ Stop</button>
          <button class="ai-hbtn" id="ai-new-btn" title="New session">↺ New</button>
          <button class="ai-hbtn danger" id="ai-close-btn" title="Close panel">✕</button>
        </div>
        <div id="ai-panel-msgs">
          <div class="ai-empty">Ask anything. Agent has access to files and tools.</div>
        </div>
        <div id="ai-panel-footer">
          <textarea id="ai-panel-input" placeholder="Message… (Ctrl+Enter to send)"></textarea>
          <div class="ai-footer-row">
            <span id="ai-status" style="font-size:9px;color:var(--text2,#8b949e);flex:1;"></span>
            <button id="ai-send-btn">Send</button>
          </div>
        </div>
      </div>
    `
    document.body.appendChild(host)

    _panel   = host
    _body    = document.getElementById('ai-panel-msgs')
    _input   = document.getElementById('ai-panel-input')
    _sendBtn = document.getElementById('ai-send-btn')
    _newBtn  = document.getElementById('ai-new-btn')
    _interruptBtn = document.getElementById('ai-interrupt-btn')
    _modelSelect = document.getElementById('ai-model-select')
    _resizer = document.getElementById('ai-panel-resizer')

    document.getElementById('ai-close-btn').onclick = function () { AiPanel.close() }
    _newBtn.onclick   = _newSession
    _sendBtn.onclick  = _sendMessage
    _interruptBtn.onclick = _interruptAgent
    _modelSelect.onchange = _onModelChange

    _input.addEventListener('keydown', function (e) {
      if (e.key === 'Enter' && e.ctrlKey) { e.preventDefault(); _sendMessage() }
    })

    _initResizer()
    _applyWidth()
  }

  // ── Width ──────────────────────────────────────────────────────────────────

  function _applyWidth() {
    document.documentElement.style.setProperty('--ai-panel-w', _panelW + 'px')
  }

  function _initResizer() {
    var dragging = false, startX, startW
    _resizer.addEventListener('mousedown', function (e) {
      dragging = true; startX = e.clientX; startW = _panelW
      document.body.style.cursor    = 'col-resize'
      document.body.style.userSelect = 'none'
      e.preventDefault()
    })
    document.addEventListener('mousemove', function (e) {
      if (!dragging) return
      var delta = startX - e.clientX   // dragging left edge: move left = wider
      var w = Math.max(PANEL_WIDTH_MIN, Math.min(PANEL_WIDTH_MAX, startW + delta))
      _panelW = w
      _applyWidth()
    })
    document.addEventListener('mouseup', function () {
      if (dragging) {
        dragging = false
        document.body.style.cursor    = ''
        document.body.style.userSelect = ''
        localStorage.setItem(STORAGE_KEY, _panelW)
      }
    })
  }

  // ── Open / Close ───────────────────────────────────────────────────────────

  function _ensureDOM() {
    if (!_panel) _buildDOM()
  }

  function _doOpen() {
    _ensureDOM()
    _open = true
    _panel.classList.add('open')
    document.body.classList.add('ai-panel-open')
    _input.focus()
    // Load models on first open
    if (_availableModels.length === 0) {
      _loadModels()
    }
  }

  function _doClose() {
    _open = false
    if (_panel) _panel.classList.remove('open')
    document.body.classList.remove('ai-panel-open')
    if (_sseSource) { _sseSource.close(); _sseSource = null }
  }

  // ── Session ────────────────────────────────────────────────────────────────

  function _loadModels() {
    fetch('/ai/providers')
      .then(function (resp) { return resp.json() })
      .then(function (data) {
        _availableModels = data.models || []
        _populateModelSelect()
      })
      .catch(function (err) {
        console.error('[ai] failed to load models:', err)
        // Fallback to default
        _availableModels = [
          { fullModel: 'kr/claude-sonnet-4.5', name: 'Claude Sonnet 4.5', available: true }
        ]
        _populateModelSelect()
      })
  }

  function _populateModelSelect() {
    _modelSelect.innerHTML = ''
    _availableModels.forEach(function (model) {
      if (!model.available) return
      var opt = document.createElement('option')
      opt.value = model.fullModel
      opt.textContent = model.name
      if (model.fullModel === _selectedModel) {
        opt.selected = true
      }
      _modelSelect.appendChild(opt)
    })
  }

  function _onModelChange() {
    _selectedModel = _modelSelect.value
    localStorage.setItem('ai_selected_model', _selectedModel)
    _setStatus('Model: ' + (_availableModels.find(function(m) { return m.fullModel === _selectedModel }) || {}).name)
    setTimeout(function() { _setStatus('') }, 2000)
  }

  function _newSession() {
    if (_sseSource) { _sseSource.close(); _sseSource = null }
    _chatId    = _mkId()
    _history   = []
    _streaming = false
    _body.innerHTML = '<div class="ai-empty">New session started.</div>'
    _input.disabled   = false
    _sendBtn.disabled = false
    _interruptBtn.disabled = true
    _setStatus('')
    // reset cwd to default (app root)
    AiPanel._cwd = AiPanel._defaultCwd || ''
  }

  // ── Interrupt ──────────────────────────────────────────────────────────────

  function _interruptAgent() {
    if (!_streaming) return

    fetch('/ai/interrupt', {
      method:  'POST',
      headers: { 'Content-Type': 'application/json' },
      body:    JSON.stringify({ chatId: _chatId }),
    }).then(function (resp) {
      return resp.json()
    }).then(function (data) {
      if (data.ok) {
        _setStatus('⏸ interrupted')
        _appendToMsg(_body.lastElementChild, '\n\n[Interrupted by user]')
      } else {
        _setStatus('⚠ interrupt failed: ' + (data.error || 'unknown'))
      }
    }).catch(function (e) {
      _setStatus('⚠ interrupt error: ' + e.message)
    })

    // Immediately disable streaming UI
    _streaming = false
    _input.disabled   = false
    _sendBtn.disabled = false
    _interruptBtn.disabled = true
    if (_sseSource) { _sseSource.close(); _sseSource = null }
  }

  // ── Context ────────────────────────────────────────────────────────────────

  function _gatherContext() {
    var parts = []
    _contextFns.forEach(function (fn) {
      try {
        var c = fn()
        if (c) parts.push(c)
      } catch (e) {}
    })
    return parts.join('\n\n')
  }

  // ── Send ───────────────────────────────────────────────────────────────────

  function _sendMessage() {
    var text = _input.value.trim()
    if (!text || _streaming) return

    // prepend context on first message
    var msg = text
    if (_history.length === 0) {
      var ctx = _gatherContext()
      if (ctx) msg = '<context>\n' + ctx + '\n</context>\n\n' + text
    }

    _input.value = ''
    _appendMsg('user', text)  // show clean text in UI
    _history.push({ role: 'user', content: msg })
    _stream(msg)
  }

  function _stream(message) {
    if (_sseSource) { _sseSource.close(); _sseSource = null }
    _streaming = true
    _input.disabled   = true
    _sendBtn.disabled = true
    _interruptBtn.disabled = false
    _setStatus('● thinking…')

    var cwd = ''
    try { if (typeof AiPanel._cwd === 'string') cwd = AiPanel._cwd } catch (e) {}

    fetch('/ai/chat', {
      method:  'POST',
      headers: { 'Content-Type': 'application/json' },
      body:    JSON.stringify({
        chatId: _chatId,
        message: message,
        cwd: cwd,
        model: _selectedModel
      }),
    }).then(function (resp) {
      if (!resp.ok) throw new Error('HTTP ' + resp.status)
      var reader  = resp.body.getReader()
      var decoder = new TextDecoder()
      var buf     = ''
      var msgEl   = _appendMsg('assistant', '')

      function read() {
        reader.read().then(function (result) {
          if (result.done) {
            _finishStream(msgEl)
            return
          }
          buf += decoder.decode(result.value, { stream: true })
          var lines = buf.split('\n')
          buf = lines.pop()
          var event = ''
          lines.forEach(function (line) {
            if (line.startsWith('event: ')) { event = line.slice(7).trim(); return }
            if (!line.startsWith('data: '))  return
            var data = line.slice(6)
            try {
              var d = JSON.parse(data)
              if (event === 'delta' && d.text) {
                _appendToMsg(msgEl, d.text)
                _setStatus('● writing…')
              }
              if (event === 'tool') {
                _appendToolCall(msgEl, d.name, d.input)
                _setStatus('⚙ ' + d.name)
              }
              if (event === 'done')  { _finishStream(msgEl); return }
              if (event === 'error') { _appendToMsg(msgEl, '\n[Error] ' + d.error); _finishStream(msgEl) }
            } catch (e) {}
          })
          read()
        }).catch(function (e) {
          _appendToMsg(msgEl, '\n[stream error] ' + e.message)
          _finishStream(msgEl)
        })
      }
      read()
    }).catch(function (e) {
      var el = _appendMsg('assistant', '[Error] ' + e.message)
      _finishStream(el)
    })
  }

  function _finishStream(msgEl) {
    _streaming        = false
    _input.disabled   = false
    _sendBtn.disabled = false
    _interruptBtn.disabled = true
    _setStatus('')
    // remove cursor
    var cursor = msgEl.querySelector('.ai-stream-cursor')
    if (cursor) cursor.remove()
    _input.focus()
  }

  // ── DOM helpers ────────────────────────────────────────────────────────────

  function _appendMsg(role, text) {
    var empty = _body.querySelector('.ai-empty')
    if (empty) empty.remove()

    var el   = document.createElement('div')
    el.className = 'ai-msg ' + role
    var body = document.createElement('div')
    body.className = 'ai-msg-body'
    body.textContent = text

    if (role === 'assistant') {
      var cursor = document.createElement('span')
      cursor.className = 'ai-stream-cursor'
      body.appendChild(cursor)
    }

    var roleEl = document.createElement('div')
    roleEl.className   = 'ai-msg-role'
    roleEl.textContent = role === 'user' ? 'You' : 'Agent'

    el.appendChild(roleEl)
    el.appendChild(body)
    _body.appendChild(el)
    _body.scrollTop = _body.scrollHeight
    return el
  }

  function _appendToMsg(msgEl, text) {
    var body   = msgEl.querySelector('.ai-msg-body')
    var cursor = body.querySelector('.ai-stream-cursor')
    if (cursor) {
      cursor.before(document.createTextNode(text))
    } else {
      body.appendChild(document.createTextNode(text))
    }
    _body.scrollTop = _body.scrollHeight
  }

  function _appendToolCall(msgEl, name, input) {
    var body = msgEl.querySelector('.ai-msg-body')
    var tc   = document.createElement('div')
    tc.className   = 'ai-tool-call'
    tc.textContent = '⚙ ' + name + '(' + JSON.stringify(input, null, 2) + ')'
    var cursor = body.querySelector('.ai-stream-cursor')
    if (cursor) cursor.before(tc)
    else body.appendChild(tc)
    _body.scrollTop = _body.scrollHeight
  }

  function _setStatus(text) {
    var el = document.getElementById('ai-status')
    if (el) el.textContent = text
  }

  // ── Public API ─────────────────────────────────────────────────────────────

  window.AiPanel = {
    _cwd: '',
    _defaultCwd: '',

    open: function (contextFn) {
      if (contextFn) _contextFns.push(contextFn)
      _doOpen()
    },

    close: function () { _doClose() },

    toggle: function (contextFn) {
      if (_open) _doClose()
      else       this.open(contextFn)
    },

    // header button: reset to default cwd then open
    toggleDefault: function () {
      this._cwd = this._defaultCwd || ''
      if (_open) _doClose()
      else       _doOpen()
    },

    setContext: function (fn) {
      if (typeof fn === 'function') _contextFns.push(fn)
    },

    setCwd: function (path) {
      this._cwd = path || ''
    },

    isOpen: function () { return _open },
  }
})()
