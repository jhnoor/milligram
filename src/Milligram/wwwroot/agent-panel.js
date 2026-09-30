import { Terminal } from './lib/xterm/xterm.mjs';
import { FitAddon } from './lib/xterm/addon-fit.mjs';
import { api } from './api.js';
import { fileLinks } from './terminal-links.js';

function stored(key) { try { return localStorage.getItem(key); } catch { return null; } }
function remember(key, value) { try { localStorage.setItem(key, value); } catch { /* Private browsing may disable storage. */ } }

export class AgentPanel {
  constructor(root, { popout = false, openSource = null, poll = true, onMeta = null } = {}) {
    this.root = root;
    this.popout = popout;
    this.openSource = openSource;
    this.onMeta = onMeta;
    this.opened = popout;
    this.retry = 0;
    this.busy = false;
    this.height = Number(stored('mg.agent.height')) || 300;
    root.innerHTML = `<div class="agent-resizer" role="separator" tabindex="0" aria-label="Resize agent panel" aria-orientation="horizontal"></div>
      <header class="agent-header">
        <h2 title="Ctrl+backtick focuses or collapses the terminal. Ctrl+C copies a selection; otherwise it interrupts. Ctrl/Cmd+click opens a file link.">Agent</h2>
        <div class="agent-status" role="status"><span class="dot" aria-hidden="true"></span><span class="agent-state">Stopped</span></div>
        <label class="agent-reader"><input type="checkbox"> Screen reader</label>
        <button class="agent-start">Start</button><button class="agent-restart">Restart</button><button class="agent-stop">Stop</button>
        <button class="agent-popout-button" title="Open this agent in a separate window">Pop out</button>
        <button class="agent-toggle" aria-expanded="false" title="Focus or collapse the agent (Ctrl+backtick)">Expand</button>
      </header>
      <div class="agent-notice" role="status" hidden></div>
      <div class="agent-body"><div class="agent-terminal" aria-label="Agent terminal"></div><div class="agent-empty">Start the agent to connect.</div></div>`;
    this.$ = (selector) => root.querySelector(selector);
    const reader = this.$('.agent-reader input');
    reader.checked = stored('mg.agent.reader') === '1';
    reader.onchange = () => {
      remember('mg.agent.reader', reader.checked ? '1' : '0');
      if (this.terminal) this.terminal.options.screenReaderMode = reader.checked;
    };
    this.$('.agent-start').onclick = () => this.control('start-agent');
    this.$('.agent-stop').onclick = () => this.control('stop-agent');
    this.$('.agent-restart').onclick = () => this.control('restart-agent');
    this.$('.agent-popout-button').onclick = () => window.open('/agent.html', '_blank', 'noopener,noreferrer');
    this.$('.agent-toggle').onclick = () => this.setOpen(!this.opened, true);
    this.wireResize();
    new ResizeObserver(() => this.fit()).observe(this.$('.agent-terminal'));
    window.addEventListener('resize', () => this.setHeight(this.height));
    document.addEventListener('keydown', (event) => {
      if (event.code !== 'Backquote' || !event.ctrlKey || event.altKey || event.metaKey || this.root.hidden) return;
      event.preventDefault();
      if (!this.popout && this.opened && this.terminal?.element?.contains(document.activeElement)) this.setOpen(false, true);
      else { this.setOpen(true, true); this.terminal?.focus(); }
    });
    document.addEventListener('visibilitychange', () => {
      if (document.hidden) this.disconnect();
      else this.refresh();
    });
    window.addEventListener('pagehide', () => this.disconnect());
    if (poll) this.timer = setInterval(() => { if (!document.hidden) this.refresh(); }, 5000);
    this.setHeight(this.height);
  }

  update(meta) {
    const access = meta.agent?.terminal;
    this.meta = meta;
    this.offline = false;
    if (this.project && this.project !== meta.root) {
      this.changedProject = true;
      this.disconnect();
      this.notice('The project changed. Reload this page before connecting to its agent.');
      this.render();
      return;
    }
    this.onMeta?.(meta);
    if (!this.project) {
      this.project = meta.root;
      this.opened = this.popout || (access?.autoOpen !== false &&
        (stored('mg.agent.collapsed') === '0' || (stored('mg.agent.collapsed') === null && meta.agent.running)));
    }
    this.root.hidden = !access?.available;
    if (this.root.hidden) this.disconnect();
    this.root.classList.toggle('collapsed', !this.opened);
    this.render();
    if (this.opened && meta.agent.running) this.connect();
    else if (!meta.agent.running) this.disconnect();
  }

  async refresh() {
    if (this.refreshing || this.changedProject) return;
    this.refreshing = true;
    try { this.update(await api.get('/api/meta')); }
    catch { this.offline = true; this.render(); }
    finally { this.refreshing = false; }
  }

  render() {
    const agent = this.meta?.agent;
    const connected = this.socket?.readyState === WebSocket.OPEN;
    const connecting = this.socket?.readyState === WebSocket.CONNECTING;
    const running = connected || agent?.running;
    const error = this.changedProject || (agent && !agent.available);
    let label = error ? 'Unavailable' : this.offline ? 'Viewer offline' : connected ? 'Running' : connecting ? 'Connecting…'
      : running && this.opened && !document.hidden ? 'Reconnecting…' : running ? 'Running' : 'Stopped';
    if (this.busy) label = 'Working…';
    this.$('.agent-state').textContent = label;
    this.$('.agent-status').dataset.state = error ? 'error' : connected || running ? 'running' : connecting ? 'connecting' : 'stopped';
    this.$('.agent-start').hidden = !!running;
    this.$('.agent-start').disabled = this.busy || !agent?.available || this.changedProject || !!this.meta?.policyError;
    this.$('.agent-stop').hidden = !running;
    this.$('.agent-stop').disabled = this.busy || this.changedProject;
    this.$('.agent-restart').disabled = this.busy || !running || !agent?.available || this.changedProject || !!this.meta?.policyError;
    this.$('.agent-toggle').textContent = this.opened ? 'Collapse' : 'Expand';
    this.$('.agent-toggle').setAttribute('aria-expanded', String(this.opened));
    this.$('.agent-empty').hidden = !!this.terminal;
    this.$('.agent-empty').textContent = this.meta?.policyError || agent?.reason || (running ? 'Connecting to the agent…' : 'Start the agent to connect.');
  }

  setOpen(opened, focus = false) {
    if (this.root.hidden) return;
    this.opened = this.popout || opened;
    if (!this.popout) remember('mg.agent.collapsed', this.opened ? '0' : '1');
    this.focusWanted = focus && this.opened;
    this.root.classList.toggle('collapsed', !this.opened);
    if (!this.opened) {
      this.disconnect();
      document.querySelector('#canvas')?.focus();
    } else {
      this.fit();
      if (focus) this.terminal?.focus();
      this.refresh();
    }
    this.render();
  }

  setHeight(height) {
    const maximum = Math.max(160, window.innerHeight - 150);
    this.height = Math.round(Math.max(160, Math.min(maximum, height)));
    this.root.style.setProperty('--agent-height', `${this.height}px`);
    const divider = this.$('.agent-resizer');
    divider.setAttribute('aria-valuemin', '160');
    divider.setAttribute('aria-valuemax', String(maximum));
    divider.setAttribute('aria-valuenow', String(this.height));
    this.fit();
  }

  wireResize() {
    const divider = this.$('.agent-resizer');
    divider.onpointerdown = (event) => {
      if (event.button !== 0) return;
      event.preventDefault();
      const from = event.clientY, height = this.height;
      divider.setPointerCapture(event.pointerId);
      divider.onpointermove = (move) => this.setHeight(height + from - move.clientY);
      divider.onpointerup = divider.onpointercancel = () => {
        divider.onpointermove = null;
        remember('mg.agent.height', String(this.height));
      };
    };
    divider.onkeydown = (event) => {
      if (!['ArrowUp', 'ArrowDown', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      this.setHeight(event.key === 'Home' ? 160 : event.key === 'End' ? window.innerHeight : this.height + (event.key === 'ArrowUp' ? 24 : -24));
      remember('mg.agent.height', String(this.height));
    };
  }

  createTerminal() {
    const focused = this.terminal?.element?.contains(document.activeElement);
    this.focusWanted ||= focused;
    this.terminal?.dispose();
    this.$('.agent-terminal').replaceChildren();
    const terminal = this.terminal = new Terminal({
      fontFamily: '"Cascadia Code", "JetBrains Mono", Menlo, Consolas, monospace', fontSize: 13,
      theme: { background: '#0f1320', foreground: '#d7deef', cursor: '#7aa2ff', selectionBackground: '#3a4466' },
      cursorBlink: true, scrollback: 5000, screenReaderMode: this.$('.agent-reader input').checked,
      disableStdin: true, logLevel: 'off',
      linkHandler: { activate: (event, uri) => {
        if (!(event.ctrlKey || event.metaKey) || !/^https?:\/\//i.test(uri)) return;
        if (window.confirm(`Open this link from the agent?\n\n${uri}`)) window.open(uri, '_blank', 'noopener,noreferrer');
      } },
    });
    this.fitAddon = new FitAddon();
    terminal.loadAddon(this.fitAddon);
    terminal.open(this.$('.agent-terminal'));
    terminal.parser.registerOscHandler(52, () => true);
    terminal.registerLinkProvider(fileLinks(terminal, async (file, line) => {
      document.activeElement?.blur();
      if (this.openSource) await this.openSource(file, line);
      else {
        const result = await api.post('/api/open', { file, line });
        if (!result.ok) this.notice(result.message);
      }
      document.querySelector('#source .close')?.focus();
    }));
    terminal.attachCustomKeyEventHandler((event) => {
      if (event.ctrlKey && event.code === 'Backquote' && !event.altKey && !event.metaKey) return false;
      return !(event.ctrlKey && !event.altKey && !event.metaKey && event.key.toLowerCase() === 'c' && terminal.hasSelection());
    });
    terminal.onData((data) => this.sendInput(new TextEncoder().encode(data)));
    terminal.onBinary((data) => this.sendInput(Uint8Array.from(data, (c) => c.charCodeAt(0) & 255)));
    terminal.onResize(({ cols, rows }) => {
      clearTimeout(this.resizeTimer);
      this.resizeTimer = setTimeout(() => this.sendControl({ type: 'resize', columns: cols, rows }), 50);
    });
    this.fit();
  }

  fit() {
    if (!this.opened || this.root.hidden || !this.terminal || !this.$('.agent-terminal').clientHeight) return;
    const size = this.fitAddon.proposeDimensions();
    if (size) this.terminal.resize(Math.max(2, Math.min(1000, size.cols)), Math.max(2, Math.min(1000, size.rows)));
  }

  connect() {
    if (this.socket || this.retryTimer || !this.opened || document.hidden || this.changedProject || this.root.hidden) return;
    const protocol = this.meta?.agent?.terminal?.protocol;
    if (!protocol?.startsWith('milligram-terminal.v2.')) {
      this.notice('The viewer terminal protocol changed. Reload this page.');
      return;
    }
    this.createTerminal();
    const terminal = this.terminal;
    const address = new URL('/api/agent/terminal', location.href);
    address.protocol = location.protocol === 'https:' ? 'wss:' : 'ws:';
    const socket = this.socket = new WebSocket(address, protocol);
    socket.binaryType = 'arraybuffer';
    socket.onopen = () => {
      if (this.socket !== socket) return;
      this.retry = 0;
      this.offline = false;
      this.notice('');
      terminal.options.disableStdin = false;
      this.sendControl({ type: 'resize', columns: terminal.cols, rows: terminal.rows });
      this.sendControl({ type: 'status' });
      if (this.focusWanted && (document.activeElement === document.body || this.root.contains(document.activeElement))) terminal.focus();
      this.focusWanted = false;
      this.render();
    };
    socket.onmessage = ({ data }) => {
      if (this.socket !== socket) return;
      if (data instanceof ArrayBuffer) {
        const bytes = new Uint8Array(data);
        // Acknowledge parsing, not network receipt, to keep the browser's pending bytes bounded.
        terminal.write(bytes, () => {
          if (bytes.length && this.socket === socket && socket.readyState === WebSocket.OPEN)
            socket.send(JSON.stringify({ type: 'ack', bytes: bytes.length }));
        });
      } else {
        try {
          const message = JSON.parse(data);
          if (message.type === 'exited') {
            this.exited = true;
            this.meta.agent.running = false;
            terminal.options.disableStdin = true;
            this.notice(`Agent exited (${message.code}). Start it again to continue.`);
          }
        } catch { this.notice('Invalid terminal response. Reconnecting…'); socket.close(); }
      }
    };
    socket.onclose = (event) => {
      if (this.socket !== socket) return;
      this.socket = null;
      terminal.options.disableStdin = true;
      if (event.code !== 1000 && !this.exited) this.notice('Connection interrupted. Reconnecting…');
      this.render();
      if (!this.exited) this.scheduleReconnect();
      else this.refresh();
    };
    socket.onerror = () => { /* onclose schedules the retry without exposing token-bearing handshake details. */ };
    this.exited = false;
    this.render();
  }

  sendInput(bytes) {
    if (this.socket?.readyState !== WebSocket.OPEN) return;
    if (bytes.length + this.socket.bufferedAmount > 256 * 1024) {
      this.notice('The terminal input is busy or the paste is too large. Try a smaller paste after it catches up.');
      return;
    }
    for (let offset = 0; offset < bytes.length; offset += 16 * 1024) this.socket.send(bytes.subarray(offset, offset + 16 * 1024));
  }

  sendControl(message) {
    if (this.socket?.readyState === WebSocket.OPEN) this.socket.send(JSON.stringify(message));
  }

  disconnect() {
    clearTimeout(this.retryTimer);
    this.retryTimer = null;
    const socket = this.socket;
    this.socket = null;
    if (this.terminal) this.terminal.options.disableStdin = true;
    if (socket && socket.readyState < WebSocket.CLOSING) socket.close(1000, 'Detached');
  }

  scheduleReconnect() {
    if (!this.opened || document.hidden || this.changedProject) return;
    clearTimeout(this.retryTimer);
    const delay = Math.min(8000, 500 * 2 ** Math.min(this.retry++, 4));
    this.retryTimer = setTimeout(() => { this.retryTimer = null; this.refresh(); }, delay);
  }

  async control(op) {
    if (this.busy || this.changedProject) return;
    this.busy = true;
    this.render();
    try {
      const result = await api.post('/api/action', { op });
      if (!result.ok) this.notice(result.message || 'Could not control the agent.');
      else {
        this.notice('');
        if (op !== 'stop-agent') this.setOpen(true, true);
      }
      await this.refresh();
    } finally { this.busy = false; this.render(); }
  }

  notice(message) {
    this.$('.agent-notice').textContent = message || '';
    this.$('.agent-notice').hidden = !message;
  }
}
