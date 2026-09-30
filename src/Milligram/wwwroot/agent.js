import { AgentPanel } from './agent-panel.js';

const panel = new AgentPanel(document.querySelector('#agent-panel'), {
  popout: true,
  onMeta(meta) {
    document.title = `Agent · ${meta.title || 'Milligram'}`;
    const unavailable = document.querySelector('#agent-unavailable');
    unavailable.hidden = !!meta.agent?.terminal?.available;
    unavailable.textContent = 'The browser terminal needs agent.host set to "milligram". Restart the viewer after changing it.';
  },
});
panel.refresh();
