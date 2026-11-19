let current;

const id = () => encodeURIComponent(document.querySelector("#incident-id").value.trim());
const status = document.querySelector("#status");
const descriptionBox = document.querySelector("#description");
const commentsBox = document.querySelector("#comments");

// Єдиний safe helper для опису й коментарів: структуру задає код, дані йдуть лише в текстовий вузол.
function createTextElement(tagName, text, className) {
  const element = document.createElement(tagName);
  element.textContent = text;
  if (className) {
    element.className = className;
  }
  return element;
}

async function load() {
  const response = await fetch(`/api/incidents/${id()}`);
  status.textContent = `GET: ${response.status}`;
  if (!response.ok) return;
  current = await response.json();
  document.querySelector("#title").textContent = current.title;

  // опис: один текстовий вузол, без розбору HTML
  descriptionBox.replaceChildren(document.createTextNode(current.description));

  // коментарі: p створює код, text потрапляє лише в textContent
  const items = (current.comments ?? []).map((comment) => createTextElement("p", comment.text));
  commentsBox.replaceChildren(...items);

  document.querySelector("#edit").elements.description.value = current.description;
}

document.querySelector("#load").addEventListener("click", load);
document.querySelector("#edit").addEventListener("submit", async event => {
  event.preventDefault();
  const response = await fetch("/api/incidents", { method: "POST", headers: { "Content-Type": "application/json" },
    body: JSON.stringify({ title: event.currentTarget.elements.title.value, description: event.currentTarget.elements.description.value,
      severity: "Low", occurredAtUtc: new Date().toISOString() }) });
  status.textContent = `POST: ${response.status}`;
  if (response.ok) {
    const created = await response.json();
    document.querySelector("#incident-id").value = created.id;
    await load();
  }
});