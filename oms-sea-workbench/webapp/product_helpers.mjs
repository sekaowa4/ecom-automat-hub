// Reusable form helpers; no historical batch data is required at runtime.
const createUrl = 'https://app.upseller.com/zh-CN/products/product-add?productType=single';
const pause = ms => new Promise(resolve => setTimeout(resolve, ms));

async function tool(call, name, args) {
  const response = await call('tools/call', { name, arguments: args });
  if (response.error || response.result?.isError)
    throw new Error(response.error?.message || response.result?.content?.map(x => x.text).join('\n'));
  return response.result?.content?.map(x => x.text || '').join('\n') || '';
}

function uid(snapshot, pattern) {
  const match = snapshot.match(pattern);
  if (!match) throw new Error(`页面上找不到控件：${pattern}`);
  return match[1];
}

const snapshot = (call, pageId) => tool(call, 'take_snapshot', { pageId });

export async function advanceToNext(call, pageId) {
  const navigation = await tool(call, 'navigate_page', { pageId, type: 'url', url: createUrl });
  if (navigation.includes('beforeunload:')) await tool(call, 'handle_dialog', { pageId, action: 'accept' });
  for (let attempt = 0; attempt < 30; attempt++) {
    const state = await snapshot(call, pageId);
    if (state.includes('创建单品') && /StaticText "SKU"\s*\n\s*uid=\S+ textbox\s*(?:\n|$)/.test(state)) return;
    await pause(1000);
  }
  throw new Error('跳转后未得到空白的创建单品表单；停止');
}

export async function uploadLocalImage(call, pageId, imagePath) {
  for (let attempt = 0; attempt < 2; attempt++) {
    let state = await snapshot(call, pageId);
    for (let menuAttempt = 0; menuAttempt < 2 && !state.includes('menuitem "本地图片"'); menuAttempt++) {
      await tool(call, 'click', { pageId, uid: uid(state, /uid=(\S+) button "图标: plus 添加图片"/) });
      state = await snapshot(call, pageId);
      if (!state.includes('menuitem "本地图片"')) {
        await pause(250);
        state = await snapshot(call, pageId);
      }
    }
    try {
      await tool(call, 'upload_file', { pageId, uid: uid(state, /uid=(\S+) menuitem "本地图片"/), filePaths: [imagePath] });
      return;
    } catch (error) {
      if (attempt || !error.message.includes('did not trigger a file chooser')) throw error;
    }
  }
}
