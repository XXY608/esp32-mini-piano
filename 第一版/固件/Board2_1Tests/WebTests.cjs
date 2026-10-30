'use strict';
const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const { parseScore, validateSong, withLyrics } = require('../Board2_1Piano/web/score-parser.js');
const { chromium } = require('playwright');
let checks = 0;
function check(value) { assert.ok(value); checks++; }
const timed = {id:'timed',title:'歌词',bpm:108,notes:[{pitch:1,octave:0,sharp:false,durationBeats:4}],lyricCues:[{startBeat:1.5,text:'整句歌词'},{startBeat:3,text:''}]};
check(validateSong(timed).lyricCues[0].text==='整句歌词');
for(const cue of [{startBeat:-1,text:'x'},{startBeat:4,text:'x'},{startBeat:1,text:'x\ny'},{startBeat:1,text:'字'.repeat(200)}]) {
  assert.throws(()=>validateSong({...timed,lyricCues:[cue]}));checks++;
}
check(withLyrics(structuredClone(timed),'').lyricCues.length===2);
check(withLyrics(structuredClone(timed),'字').lyricCues===undefined);
const notes = parseScore('\uFEFF（#1_.） 0_ | 【5__】 5 — —\n7.');
check(notes.length === 5 && notes[0].octave === -1 && notes[0].sharp && notes[0].durationBeats === .75);
check(notes[2].octave === 1 && notes[2].durationBeats === .25 && notes[3].durationBeats === 3);
check(notes[2].barBefore === true && notes[4].lineBefore === true);
const breaks = parseScore('1 2 | 3 4\r\n5 6 |\n7 0');
check(breaks[2].barBefore && breaks[4].lineBefore && breaks[6].barBefore && breaks[6].lineBefore);
for (const text of ['', '— 1', '【1)', '#0', '8', '1___', '（0）', '1foo', '【#1】bad', Array(1026).fill('1').join(' ')]) { assert.throws(() => parseScore(text)); checks++; }
const song = {id:'test',title:'中文测试',bpm:90,notes}; check(validateSong(song) === song);
const lyricSong = {...song,notes:parseScore('1 — 0 2\n3')};
withLyrics(lyricSong,'长 _ 音'); check(lyricSong.notes[0].lyric==='长' && !lyricSong.notes[1].lyric && !lyricSong.notes[2].lyric && lyricSong.notes[3].lyric==='音');
assert.throws(()=>withLyrics(lyricSong,'长 音')); checks++;
withLyrics(lyricSong,''); check(lyricSong.notes.every(n=>!n.lyric));
for(const lyric of [null,12,'bad\nline','x'.repeat(25)]) { assert.throws(()=>validateSong({...song,notes:[{...notes[2],lyric}]})); checks++; }
assert.throws(()=>validateSong({...song,notes:[{...notes[1],lyric:'休'}]})); checks++;
check(validateSong({...song,timeSignature:'6/8'}).timeSignature==='6/8');
for (const change of [{id:'../x'},{bpm:19},{bpm:90.5},{notes:[]},{title:''},{timeSignature:'5/3'},{timeSignature:44},{notes:[{...notes[0],lineBefore:1}]}]) { assert.throws(() => validateSong({...song,...change})); checks++; }

(async () => {
  const output = process.argv[2]; if (output) fs.mkdirSync(output, {recursive:true});
  const sketch = path.join(__dirname,'../Board2_1Piano');
  const html = fs.readFileSync(path.join(sketch,'web/index.html'),'utf8').replace('/* SCORE_PARSER */',fs.readFileSync(path.join(sketch,'web/score-parser.js'),'utf8'));
  const header = fs.readFileSync(path.join(sketch,'WebUi.h'),'utf8'); check(header.includes(html));
  const displayLabels = ['DisplayText.h','MenuScreen.h','ScoreScreen.h','App.cpp','BuiltinSongs.h'].map(file=>fs.readFileSync(path.join(sketch,file),'utf8')).join('\n');
  const fontHeader = fs.readFileSync(path.join(sketch,'ChineseFont.h'),'utf8');
  const missingGlyphs = [...new Set([...displayLabels].filter(c=>c.codePointAt(0)>127))].filter(c=>!fontHeader.includes('0x'+c.codePointAt(0).toString(16).padStart(4,'0')+','));
  check(missingGlyphs.length===0);
  const browser = await chromium.launch({headless:true,channel:'chrome'});
  try {
    const page = await browser.newPage({viewport:{width:390,height:844}});
    let pendingSample=null, storedSample=null, failSampleChunk=false, holdSampleChunk=false;
    const realSample=fs.readFileSync(path.join(process.env.PIANO_SAMPLE_DIRECTORY,'3.hps'));

    const errors = []; page.on('pageerror', e => errors.push(e.message));
    let songs = [{id:'demo-scale',title:'音阶练习（内置）',bpm:100,noteCount:16}];
    const requests = [];
    const state = {ok:true,firmwareVersion:'0.6.9-board2_1',storage:'SD + flash fallback',audioReady:true,controlsReady:true,psramBytes:8388608,playerMode:'manual',currentMidi:-1,expectedMidi:-1,noteCount:0,noteIndex:0,keyMask:0,volumePercent:80,bpm:120,transpose:0,instrument:0,metronome:false,repeat:false,showLyrics:false,paused:false};
    let audioFailure=false, outputFailure=false, phoneOwner=0, phoneRoute=true;
    await page.route('http://piano.test/**', async route => {
      const req = route.request(); const pathname = new URL(req.url()).pathname;
      if (pathname === '/') return route.fulfill({status:200,contentType:'text/html; charset=utf-8',body:html});
      const data = req.headers()['content-type']?.startsWith('application/octet-stream') ? req.postDataBuffer() : req.postData() ? JSON.parse(req.postData()) : {};
      requests.push({pathname,method:req.method(),data:Buffer.isBuffer(data)?{bytes:data.length}:data});
      let result = {ok:true,message:'ok'};
      if(pathname==='/api/samples/begin') {
        check(data.instrument===3 && data.bytes===realSample.length && /^[0-9a-f]{8}$/.test(data.crc32));
        pendingSample={session:0xfedcba98,offset:0,chunks:[]};result={ok:true,session:pendingSample.session,offset:0};
      }
      if(pathname==='/api/samples/chunk') {
        // Pinned WebServer 2.0.17 does not parse URL args on its raw POST path.
        // Model that transport behavior rather than accepting query metadata.
        const headers=req.headers(),session=headers['x-sample-session']||'',offset=headers['x-sample-offset']||'';
        if(!/^[0-9]{1,10}$/.test(session) || Number(session)>0xffffffff || !pendingSample || Number(session)!==pendingSample.session || !/^[0-9]{1,7}$/.test(offset))
          return route.fulfill({status:400,contentType:'application/json',body:JSON.stringify({ok:false,message:'invalid sample chunk or session'})});
        if(holdSampleChunk) return;
        if(failSampleChunk) return route.fulfill({status:503,contentType:'application/json',body:JSON.stringify({ok:false,message:'test SD write failure'})});
        assert.equal(new URL(req.url()).search,'');
        assert.equal(Number(offset),pendingSample.offset);
        assert.ok(Buffer.isBuffer(data) && data.length>0 && data.length<=65536);
        pendingSample.chunks.push(data);pendingSample.offset+=data.length;result={ok:true,offset:pendingSample.offset};
      }
      if(pathname==='/api/samples/finish') {
        check(pendingSample && data.session===pendingSample.session && pendingSample.offset===realSample.length);
        storedSample=Buffer.concat(pendingSample.chunks);check(storedSample.equals(realSample));pendingSample=null;
      }
      if(pathname==='/api/samples/cancel') {if(pendingSample && data.session===pendingSample.session) pendingSample=null;}
      if(pathname==='/api/audio/output') {
        if(outputFailure) return route.fulfill({status:400,contentType:'application/json',body:JSON.stringify({ok:false})});
        if(data.enabled) {phoneOwner=data.session;phoneRoute=true;}
        else if(phoneOwner===data.session) phoneOwner=0;
      }
      if(pathname==='/api/audio/events') {
        if(audioFailure) return route.fulfill({status:503,body:'offline'});
        assert.ok(Number(new URL(req.url()).searchParams.get('session'))===phoneOwner && phoneOwner>0);
        result={phoneOutput:phoneRoute,reset:true,events:new URL(req.url()).searchParams.get('after')==='0'?[[1,100,72,50,0,1,0]]:[]};
      }
      if (pathname === '/api/status') result = state;
      if (pathname === '/api/songs') result = {ok:true,songs};
      if (pathname === '/api/songs/upload') { validateSong(data); songs.push({id:data.id,title:data.title,bpm:data.bpm,noteCount:data.notes.length}); }
      if (pathname === '/api/device/settings') Object.assign(state,data);
      if (pathname === '/api/device/volume') state.volumePercent = data.volumePercent;
      if (pathname === '/api/song/play') {state.playerMode = 'play';state.noteCount=16;}
      if (pathname === '/api/song/pause') state.paused=true;
      if (pathname === '/api/song/resume') state.paused=false;
      if (pathname === '/api/song/stop') state.playerMode='manual';
      if (req.method()==='DELETE') songs=songs.filter(s => s.id!==pathname.split('/').pop());
      await route.fulfill({status:200,contentType:'application/json',body:JSON.stringify(result)});
    });
    await page.goto('http://piano.test/'); await page.getByText('已连接',{exact:true}).waitFor();
    pendingSample={session:0xfedcba98,offset:0,chunks:[]};
    const rejectedChunks=await page.evaluate(async()=>{
      const token=String(0xfedcba98),base={'Content-Type':'application/octet-stream'};
      const requests=[
        ['/api/samples/chunk?session='+token+'&offset=0',base],
        ['/api/samples/chunk',{...base,'X-Sample-Session':'1','X-Sample-Offset':'0'}],
        ['/api/samples/chunk',{...base,'X-Sample-Session':token,'X-Sample-Offset':'-1'}]
      ];
      return Promise.all(requests.map(async([url,headers])=>{
        const response=await fetch(url,{method:'POST',headers,body:new Uint8Array([1])});
        return {status:response.status,...await response.json()};
      }));
    });
    for(const result of rejectedChunks) check(result.status===400 && result.ok===false);
    check(pendingSample.offset===0 && pendingSample.chunks.length===0);pendingSample=null;
    await page.locator('#phoneAudioStart').click();
    await page.waitForFunction(()=>window.phoneAudio.enabled && window.phoneAudio.voice && window.phoneAudio.voice.osc.frequency.value>500);
    check(await page.evaluate(()=>Math.abs(window.phoneAudio.voice.osc.frequency.value-523.251)<.01));
    check(phoneOwner>0 && state.volumePercent===80);
    phoneRoute=false;
    await page.waitForFunction(()=>window.phoneAudio.phoneRoute===false && window.phoneAudio.voices.size===0);
    check(phoneOwner>0 && await page.evaluate(()=>window.phoneAudio.enabled && !window.phoneAudio.voice));
    phoneRoute=true;
    await page.waitForFunction(()=>window.phoneAudio.phoneRoute===true && window.phoneAudio.voice);
    check(await page.evaluate(()=>Math.abs(window.phoneAudio.voice.osc.frequency.value-523.251)<.01));
    await page.locator('#phoneAudioStop').click();check(await page.evaluate(()=>!window.phoneAudio.enabled && window.phoneAudio.voices.size===0));
    await page.evaluate(()=>window.phoneAudio.releasing);check(phoneOwner===0 && state.volumePercent===80);
    audioFailure=true;await page.locator('#phoneAudioStart').click();await page.waitForFunction(()=>document.querySelector('#phoneAudioState').textContent.includes('连接中断'));check(await page.evaluate(()=>!window.phoneAudio.enabled));audioFailure=false;
    await page.evaluate(()=>window.phoneAudio.releasing);check(phoneOwner===0);
    outputFailure=true;await page.locator('#phoneAudioStart').click();await page.waitForFunction(()=>document.querySelector('#phoneAudioState').textContent.includes('无法切换机身输出'));check(await page.evaluate(()=>!window.phoneAudio.enabled && window.phoneAudio.voices.size===0));outputFailure=false;
    await page.locator('#phoneAudioStart').click();await page.waitForFunction(()=>window.phoneAudio.enabled && window.phoneAudio.voice);
    await page.evaluate(()=>{Object.defineProperty(document,'hidden',{value:true,configurable:true});document.dispatchEvent(new Event('visibilitychange'));});
    await page.evaluate(()=>window.phoneAudio.releasing);check(phoneOwner===0 && await page.evaluate(()=>!window.phoneAudio.enabled && window.phoneAudio.voices.size===0));
    await page.evaluate(()=>{delete document.hidden;});
    await page.getByRole('button',{name:'播放',exact:true}).click(); await page.waitForFunction(() => document.querySelector('#status').textContent.includes('自动播放'));
    await page.locator('#pause').click(); await page.waitForFunction(() => document.querySelector('#status').textContent.includes('已暂停'));
    await page.locator('#resume').click(); await page.waitForFunction(() => !document.querySelector('#status').textContent.includes('已暂停'));
    await page.selectOption('#instrument','2'); await page.locator('#bpm').fill('90'); await page.locator('#metronome').check(); await page.locator('#showLyrics').check(); await page.locator('#apply').click();
    await page.waitForFunction(() => document.querySelector('#message').textContent==='设置已应用'); check(state.instrument===2&&state.bpm===90&&state.metronome&&state.showLyrics);
    check(await page.locator('#instrument option').count()===11);
    for(const id of ['4','10','3','2']) {
      await page.waitForFunction(()=>!busy && !polling);
      await page.locator('#instrument').focus();
      await page.selectOption('#instrument',id); await page.locator('#apply').click();
      await page.waitForFunction(() => document.querySelector('#message').textContent==='设置已应用');
      await page.waitForFunction(()=>!busy && !polling);
      check(state.instrument===Number(id));
    }
    await page.locator('#volume').fill('55'); await page.locator('#volume').dispatchEvent('change'); await page.waitForTimeout(150); check(state.volumePercent===55);
    await page.waitForFunction(()=>!busy && !polling);
    await page.locator('#sampleFiles').setInputFiles({name:'3.hps',mimeType:'application/octet-stream',buffer:realSample});
    await page.locator('#sampleUpload').click();
    await page.waitForFunction(()=>document.querySelector('#sampleResult').textContent.includes('已上传并校验 1 个音色'),{},{timeout:60000});
    check(storedSample && storedSample.equals(realSample));check(await page.locator('#sampleProgress').getAttribute('value')==='100');
    await page.waitForFunction(()=>!busy && !polling);
    failSampleChunk=true;await page.locator('#sampleUpload').click();
    await page.waitForFunction(()=>document.querySelector('#sampleResult').textContent.includes('test SD write failure'));
    await page.waitForFunction(()=>!busy && !polling);check(pendingSample===null && storedSample.equals(realSample));failSampleChunk=false;
    const before=requests.filter(r=>r.pathname==='/api/samples/chunk').length;
    holdSampleChunk=true;await page.locator('#sampleUpload').click();
    for(let i=0;i<100 && requests.filter(r=>r.pathname==='/api/samples/chunk').length===before;i++) await page.waitForTimeout(20);
    check(pendingSample!==null);await page.locator('#sampleCancel').click();
    await page.waitForFunction(()=>document.querySelector('#sampleResult').textContent.includes('上传已取消'));
    await page.waitForFunction(()=>!busy && !polling);check(pendingSample===null && storedSample.equals(realSample));holdSampleChunk=false;
    await page.locator('#sampleFiles').setInputFiles({name:'bad.hps',mimeType:'application/octet-stream',buffer:Buffer.from('bad')});
    await page.locator('#sampleUpload').click();await page.waitForFunction(()=>document.querySelector('#sampleResult').textContent.includes('音色大小不正确'));
    await page.waitForFunction(()=>!busy && !polling);check(pendingSample===null && storedSample.equals(realSample));
    check(await page.locator('[id^=record]').count()===0);
    check(!html.includes('/api/record/'));
    check(!requests.some(r=>r.pathname.startsWith('/api/record/')));
    await page.locator('#file').setInputFiles({name:'测试曲_源谱 BPM：90.txt',mimeType:'text/plain',buffer:Buffer.from('1 2 | 【3_】\n（#4_.） 0 —')});
    await page.waitForFunction(() => document.querySelector('#previewResult').textContent.includes('检查通过'));
    check(await page.locator('#title').inputValue()==='测试曲'); check(await page.locator('#importBpm').inputValue()==='90');
    await page.locator('#importMeter').fill('3/4');
    await page.locator('#lyrics').fill('一 闪 _ 星');
    await page.locator('#upload').click(); await page.getByText('测试曲',{exact:true}).waitFor(); check(songs.length===2);
    const uploaded = requests.find(r=>r.pathname==='/api/songs/upload').data;
    check(uploaded.timeSignature==='3/4' && uploaded.notes[2].barBefore && uploaded.notes[3].lineBefore);
    check(uploaded.notes[0].lyric==='一' && uploaded.notes[1].lyric==='闪' && !uploaded.notes[2].lyric && uploaded.notes[3].lyric==='星' && !uploaded.notes[4].lyric);
    page.once('dialog',dialog=>dialog.accept()); await page.getByRole('button',{name:'删除',exact:true}).click(); await page.waitForTimeout(200); check(songs.length===1);
    await page.locator('#file').setInputFiles({name:'歌词.json',mimeType:'application/json',buffer:Buffer.from(JSON.stringify(uploaded))});
    await page.waitForFunction(()=>document.querySelector('#lyrics').value==='一 闪 _ 星');
    await page.locator('#upload').click(); await page.getByText('歌曲已上传',{exact:true}).waitFor();
    check(requests.filter(r=>r.pathname==='/api/songs/upload')[1].data.notes[3].lyric==='星');
    const beforeBad=requests.filter(r=>r.pathname==='/api/songs/upload').length;
    await page.locator('#lyrics').fill('缺'); await page.locator('#upload').click();
    await page.waitForFunction(()=>document.querySelector('#message').textContent.includes('歌词需要'));
    check(requests.filter(r=>r.pathname==='/api/songs/upload').length===beforeBad);
    await page.locator('#lyrics').fill('');
    await page.locator('#score').fill('8 nope'); await page.locator('#preview').click(); await page.waitForFunction(() => document.querySelector('#message').textContent.includes('不支持的音符'));
    check(await page.evaluate(() => document.documentElement.scrollWidth <= innerWidth));
    check(errors.length===0);
    if (output) {await page.screenshot({path:path.join(output,'web-mobile.png'),fullPage:true}); fs.writeFileSync(path.join(output,'web-requests.json'),JSON.stringify(requests,null,2));}
    console.log(`RESULT web/parser ${checks} checks passed; mocked API only, no hardware`);
  } finally { await browser.close(); }
})().catch(error=>{console.error(error);process.exitCode=1;});
