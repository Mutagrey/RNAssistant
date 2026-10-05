let count = 0;
const display = document.getElementById('count');
const render = () => { display.textContent = String(count); };
document.getElementById('plus').addEventListener('click', () => { count += increment; render(); });
document.getElementById('minus').addEventListener('click', () => { count -= 1; render(); });
document.getElementById('reset').addEventListener('click', () => { count = 0; render(); });
