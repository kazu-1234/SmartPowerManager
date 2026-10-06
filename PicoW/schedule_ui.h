#pragma once
// 共通スケジュール一覧ページ（SPM PicoW / URC 同一内容）

inline void sendScheduleUiPage(WiFiClient &client, const char *version)
{
    client.print(F("HTTP/1.1 200 OK\r\nContent-Type: text/html; charset=utf-8\r\nConnection: close\r\n\r\n"));
    client.print(F("<!DOCTYPE html><html lang=ja><head><meta charset=utf-8>"
                   "<meta name=viewport content=\"width=device-width,initial-scale=1\">"
                   "<title>SPM Schedule</title><style>"
                   "body{font-family:system-ui,sans-serif;margin:16px;background:#f4f6f8;color:#1a2330}"
                   "h1{font-size:1.15rem;margin:0 0 8px}h2{font-size:1rem;margin:16px 0 8px}"
                   ".meta{color:#5b6b7c;font-size:.85rem;margin-bottom:12px}"
                   "ul{list-style:none;padding:0;margin:0}"
                   "li{background:#fff;border:1px solid #c5d0dc;border-radius:10px;padding:10px 12px;margin:0 0 8px}"
                   ".row{display:flex;justify-content:space-between;gap:8px;flex-wrap:wrap}"
                   ".muted{color:#5b6b7c;font-size:.85rem}"
                   "a{color:#0b57d0}"
                   "</style></head><body>"));
    client.print(F("<h1>スケジュール一覧</h1><div class=meta>v"));
    client.print(version ? version : "");
    client.print(F(" · <span id=mac>-</span> · <span id=clk>-</span></div>"));
    client.print(F("<p class=muted id=st>読込中…</p>"));
    client.print(F("<h2>起動スケジュール</h2><ul id=wake></ul>"));
    client.print(F("<h2>3分前 WoL</h2><ul id=wol></ul>"));
    client.print(F("<p class=muted><a href=/>戻る</a></p>"));
    client.print(F("<script>"
                   "const WD=['月','火','水','木','金','土','日'];"
                   "function $(id){return document.getElementById(id)}"
                   "function actLabel(a){return a==='shutdown'?'シャットダウン':a==='restart'?'再起動':a}"
                   "function ruleLabel(r){"
                   "if(r.type==='daily')return actLabel(r.action)+'（毎日 '+String(r.hour).padStart(2,'0')+':'+String(r.minute).padStart(2,'0')+'）';"
                   "if(r.type==='weekly')return actLabel(r.action)+'（毎週 '+WD[r.weekday|0]+' '+String(r.hour).padStart(2,'0')+':'+String(r.minute).padStart(2,'0')+'）';"
                   "if(r.type==='onetime')return actLabel(r.action)+'（一回限り '+r.year+'-'+String(r.month).padStart(2,'0')+'-'+String(r.day).padStart(2,'0')+' '+String(r.hour).padStart(2,'0')+':'+String(r.minute).padStart(2,'0')+'）';"
                   "return JSON.stringify(r)}"
                   "async function load(){"
                   "try{const j=await(await fetch('/get_schedule')).json();"
                   "$('mac').textContent='MAC '+(j.mac||'-');"
                   "$('clk').textContent=j.time_ok?'NTP OK':'NTP 未同期';"
                   "const wake=$('wake');wake.innerHTML='';"
                   "if(j.daily&&j.daily.enabled){const li=document.createElement('li');li.textContent='毎日 '+String(j.daily.hour).padStart(2,'0')+':'+String(j.daily.minute).padStart(2,'0');wake.appendChild(li)}"
                   "(j.weekly||[]).forEach(w=>{const li=document.createElement('li');li.textContent='毎週 '+WD[w.weekday|0]+' '+String(w.hour).padStart(2,'0')+':'+String(w.minute).padStart(2,'0');wake.appendChild(li)});"
                   "(j.onetime||[]).forEach(o=>{const li=document.createElement('li');li.textContent='一回限り '+o.year+'-'+String(o.month).padStart(2,'0')+'-'+String(o.day).padStart(2,'0')+' '+String(o.hour).padStart(2,'0')+':'+String(o.minute).padStart(2,'0');wake.appendChild(li)});"
                   "if(!wake.children.length){const li=document.createElement('li');li.className='muted';li.textContent='なし';wake.appendChild(li)}"
                   "const wol=$('wol');wol.innerHTML='';"
                   "(j.auto_wol_rules||[]).forEach(r=>{const li=document.createElement('li');"
                   "li.innerHTML='<div class=row><span>'+ruleLabel(r)+'</span><span class=muted>次: '+(r.next||'-')+'</span></div>';"
                   "wol.appendChild(li)});"
                   "if(!wol.children.length){const li=document.createElement('li');li.className='muted';li.textContent='なし';wol.appendChild(li)}"
                   "$('st').textContent='更新 '+new Date().toLocaleTimeString();"
                   "}catch(e){$('st').textContent='取得失敗'}}"
                   "load();setInterval(load,15000);"
                   "</script></body></html>"));
}

// WebServer 用（PicoW）: HTML 本文のみ
inline String scheduleUiHtmlBody(const char *version)
{
    String s;
    s.reserve(3500);
    s += F("<!DOCTYPE html><html lang=ja><head><meta charset=utf-8>"
           "<meta name=viewport content=\"width=device-width,initial-scale=1\">"
           "<title>SPM Schedule</title><style>"
           "body{font-family:system-ui,sans-serif;margin:16px;background:#f4f6f8;color:#1a2330}"
           "h1{font-size:1.15rem;margin:0 0 8px}h2{font-size:1rem;margin:16px 0 8px}"
           ".meta{color:#5b6b7c;font-size:.85rem;margin-bottom:12px}"
           "ul{list-style:none;padding:0;margin:0}"
           "li{background:#fff;border:1px solid #c5d0dc;border-radius:10px;padding:10px 12px;margin:0 0 8px}"
           ".row{display:flex;justify-content:space-between;gap:8px;flex-wrap:wrap}"
           ".muted{color:#5b6b7c;font-size:.85rem}"
           "</style></head><body>");
    s += F("<h1>スケジュール一覧</h1><div class=meta>v");
    s += version ? version : "";
    s += F(" · <span id=mac>-</span> · <span id=clk>-</span></div>"
           "<p class=muted id=st>読込中…</p>"
           "<h2>起動スケジュール</h2><ul id=wake></ul>"
           "<h2>3分前 WoL</h2><ul id=wol></ul>"
           "<script>"
           "const WD=['月','火','水','木','金','土','日'];"
           "function $(id){return document.getElementById(id)}"
           "function actLabel(a){return a==='shutdown'?'シャットダウン':a==='restart'?'再起動':a}"
           "function ruleLabel(r){"
           "if(r.type==='daily')return actLabel(r.action)+'（毎日 '+String(r.hour).padStart(2,'0')+':'+String(r.minute).padStart(2,'0')+'）';"
           "if(r.type==='weekly')return actLabel(r.action)+'（毎週 '+WD[r.weekday|0]+' '+String(r.hour).padStart(2,'0')+':'+String(r.minute).padStart(2,'0')+'）';"
           "if(r.type==='onetime')return actLabel(r.action)+'（一回限り '+r.year+'-'+String(r.month).padStart(2,'0')+'-'+String(r.day).padStart(2,'0')+' '+String(r.hour).padStart(2,'0')+':'+String(r.minute).padStart(2,'0')+'）';"
           "return JSON.stringify(r)}"
           "async function load(){"
           "try{const j=await(await fetch('/get_schedule')).json();"
           "$('mac').textContent='MAC '+(j.mac||'-');"
           "$('clk').textContent=j.time_ok?'NTP OK':'NTP 未同期';"
           "const wake=$('wake');wake.innerHTML='';"
           "if(j.daily&&j.daily.enabled){const li=document.createElement('li');li.textContent='毎日 '+String(j.daily.hour).padStart(2,'0')+':'+String(j.daily.minute).padStart(2,'0');wake.appendChild(li)}"
           "(j.weekly||[]).forEach(w=>{const li=document.createElement('li');li.textContent='毎週 '+WD[w.weekday|0]+' '+String(w.hour).padStart(2,'0')+':'+String(w.minute).padStart(2,'0');wake.appendChild(li)});"
           "(j.onetime||[]).forEach(o=>{const li=document.createElement('li');li.textContent='一回限り '+o.year+'-'+String(o.month).padStart(2,'0')+'-'+String(o.day).padStart(2,'0')+' '+String(o.hour).padStart(2,'0')+':'+String(o.minute).padStart(2,'0');wake.appendChild(li)});"
           "if(!wake.children.length){const li=document.createElement('li');li.className='muted';li.textContent='なし';wake.appendChild(li)}"
           "const wol=$('wol');wol.innerHTML='';"
           "(j.auto_wol_rules||[]).forEach(r=>{const li=document.createElement('li');"
           "li.innerHTML='<div class=row><span>'+ruleLabel(r)+'</span><span class=muted>次: '+(r.next||'-')+'</span></div>';"
           "wol.appendChild(li)});"
           "if(!wol.children.length){const li=document.createElement('li');li.className='muted';li.textContent='なし';wol.appendChild(li)}"
           "$('st').textContent='更新 '+new Date().toLocaleTimeString();"
           "}catch(e){$('st').textContent='取得失敗'}}"
           "load();setInterval(load,15000);"
           "</script></body></html>");
    return s;
}
