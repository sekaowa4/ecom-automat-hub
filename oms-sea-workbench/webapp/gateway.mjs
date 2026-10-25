import readline from "node:readline";
import { probeBrowser } from "./browser.mjs";
import { ShippingWorkflow } from "./runner.mjs";

const clients = new Map();
const send = (value) => process.stdout.write(JSON.stringify(value) + "\n");
let active = null,
  probing = false,
  shuttingDown = false;
const input = readline.createInterface({ input: process.stdin });
function close() {
  if (shuttingDown) return;
  shuttingDown = true;
  active?.worker.approvals.receive({ type: "pause" });
  const stop = () => {
    for (const client of clients.values()) client.close();
    input.close();
  };
  if (active) active.promise.finally(stop);
  else stop();
}
input.on("line", async (line) => {
  let request;
  try {
    request = JSON.parse(line);
    if (request.command === "control") {
      if (active?.id === request.job_id)
        active.worker.approvals.receive(request.message);
      return;
    }
    if (request.command === "probe") {
      if (active || probing)
        throw new Error("浏览器正在处理批次或读取连接，请稍后重试");
      probing = true;
      try {
        send({ reply_to: request.id, result: await probeBrowser(clients) });
      } finally {
        probing = false;
      }
      return;
    }
    if (request.command === "run") {
      if (active || probing || shuttingDown)
        throw new Error("上一项操作尚未结束，请稍后继续");
      const worker = new ShippingWorkflow(
        request.input,
        (event) => send({ job_id: request.input.id, event }),
        clients,
      );
      active = { id: request.input.id, worker, promise: null };
      send({ reply_to: request.id, result: { accepted: true } });
      active.promise = worker.run().finally(() => {
        active = null;
      });
      return;
    }
    if (request.command === "shutdown") return close();
    throw new Error("无法识别的工作台操作");
  } catch (error) {
    send({ reply_to: request?.id, error: error.message });
  }
});
input.on("close", close);
