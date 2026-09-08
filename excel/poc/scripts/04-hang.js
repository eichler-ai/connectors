// What does a hung script do to the task pane and the bridge deadline? Run with -timeout 5s.
// Expect: bridge reports deadline; task pane is frozen (busy loop) until Excel/Chrome kills it or the pane is reloaded.
const until = Date.now() + 60000;
while (Date.now() < until) {}
return "unreachable";
