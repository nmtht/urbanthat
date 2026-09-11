#!/usr/bin/env node
/**
 * Minimal Phase 1 receiver probe. Run after loading UrbanBridgePlugin in Rhino:
 *     node tools/ws-probe.mjs
 */
const url = process.env.URBANBRIDGE_URL ?? 'ws://localhost:7890';
const socket = new WebSocket(url);
let received = 0;

socket.addEventListener('open', () => {
  console.log(`[probe] connected to ${url}`);
  socket.send(JSON.stringify({ type: 'request_full_sync' }));
});

socket.addEventListener('message', ({ data }) => {
  try {
    const message = JSON.parse(data);
    received += 1;
    const count = Array.isArray(message.objects) ? ` objects=${message.objects.length}` : '';
    const id = message.id ? ` id=${message.id}` : '';
    console.log(`[probe] #${received} type=${message.type}${count}${id}`);
  } catch {
    console.error('[probe] received non-JSON message');
  }
});

socket.addEventListener('error', () => console.error(`[probe] connection error: is Rhino running and the plugin loaded at ${url}?`));
socket.addEventListener('close', ({ code }) => console.log(`[probe] closed (code ${code})`));
