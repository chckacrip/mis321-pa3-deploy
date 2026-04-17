const app = document.getElementById('app');

// Build layout using DOM methods — no HTML strings
const container = document.createElement('div');
container.className = 'container';

// Header
const header = document.createElement('header');

const headerIcon = document.createElement('div');
headerIcon.className = 'header-icon';
headerIcon.textContent = '🗄️';

const headerText = document.createElement('div');
headerText.className = 'header-text';
const h1 = document.createElement('h1');
h1.textContent = 'DB Assistant';
const subtitle = document.createElement('p');
subtitle.textContent = 'Ask questions about the TQL_2012 database in plain English';
headerText.appendChild(h1);
headerText.appendChild(subtitle);

header.appendChild(headerIcon);
header.appendChild(headerText);

// Messages area
const messagesEl = document.createElement('div');
messagesEl.className = 'messages';

// Loading indicator with animated dots
const loadingEl = document.createElement('div');
loadingEl.className = 'loading hidden';
const loadingText = document.createElement('span');
loadingText.textContent = 'Thinking';
const dotsEl = document.createElement('div');
dotsEl.className = 'loading-dots';
for (let i = 0; i < 3; i++) {
  const dot = document.createElement('span');
  dotsEl.appendChild(dot);
}
loadingEl.appendChild(loadingText);
loadingEl.appendChild(dotsEl);

// Input row
const inputRow = document.createElement('div');
inputRow.className = 'input-row';

const inputEl = document.createElement('input');
inputEl.type = 'text';
inputEl.placeholder = 'e.g. Show me the top 5 customers by total orders';

const sendBtn = document.createElement('button');
sendBtn.textContent = 'Send';

inputRow.appendChild(inputEl);
inputRow.appendChild(sendBtn);

container.appendChild(header);
container.appendChild(messagesEl);
container.appendChild(loadingEl);
container.appendChild(inputRow);
app.appendChild(container);

// Append a chat bubble
function appendMessage(role, text, sql) {
  const bubble = document.createElement('div');
  bubble.classList.add('message', role);

  const content = document.createElement('p');
  content.textContent = text;
  bubble.appendChild(content);

  if (sql) {
    const details = document.createElement('details');
    const summary = document.createElement('summary');
    summary.textContent = 'View SQL query';
    const code = document.createElement('pre');
    code.textContent = sql;
    details.appendChild(summary);
    details.appendChild(code);
    bubble.appendChild(details);
  }

  messagesEl.appendChild(bubble);
  messagesEl.scrollTop = messagesEl.scrollHeight;
}

// Send message to backend
async function sendMessage() {
  const text = inputEl.value.trim();
  if (!text) return;

  inputEl.value = '';
  appendMessage('user', text);
  loadingEl.classList.remove('hidden');
  sendBtn.disabled = true;

  try {
    const res = await fetch('/api/chat', {
      method: 'POST',
      headers: { 'Content-Type': 'application/json' },
      body: JSON.stringify({ message: text })
    });

    const data = await res.json();
    appendMessage('assistant', data.reply, data.sql);
  } catch (err) {
    appendMessage('assistant', 'Something went wrong. Please try again.');
    console.error(err);
  } finally {
    loadingEl.classList.add('hidden');
    sendBtn.disabled = false;
  }
}

sendBtn.addEventListener('click', sendMessage);
inputEl.addEventListener('keydown', e => {
  if (e.key === 'Enter') sendMessage();
});
