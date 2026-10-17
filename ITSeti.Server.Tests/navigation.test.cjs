const assert = require('node:assert/strict');
const fs = require('node:fs');
const path = require('node:path');
const vm = require('node:vm');
const elements = new Map();
function element() { return { value:'', children:[], rows:[], open:false, disabled:false,
  addEventListener(){}, classList:{toggle(){},remove(){},add(){}}, setAttribute(){}, append(...items){this.children.push(...items);},
  replaceChildren(...items){this.children=items;}, showModal(){this.open=true;}, close(){this.open=false;} }; }
const kinds = ['events','cpu','memory','temperature','voltage','disks','identity','other'].map(value => ({checked:true,dataset:{monitorKind:value}}));
const entries = [null]; let position = 0;
const history = { get state(){return entries[position];}, replaceState(value){entries[position]=structuredClone(value);},
  pushState(value){entries.splice(++position);entries[position]=structuredClone(value);}, back(){position=Math.max(0,position-1);} };
const context = vm.createContext({console, URLSearchParams, window:{history}, document:{
  querySelector:()=>({content:'fixture'}), getElementById:id=>{if(!elements.has(id))elements.set(id,element());return elements.get(id);},
  createElement:()=>element(),
  querySelectorAll:selector=>selector==='[data-monitor-kind]'?kinds:[]
}});
const source=fs.readFileSync(path.join(__dirname,'../ITSeti.Server/wwwroot/app.js'),'utf8');
vm.runInContext(source.slice(0,source.indexOf("for (const button of document.querySelectorAll('[data-view]'))")),context);
vm.runInContext(`loadDevices=async()=>{}; fillSites=()=>{}; refreshSearchSelect=()=>{};
  loadMonitoring=async()=>{}; loadConnections=async()=>{}; loadApplicationErrors=async()=>{};
  loadTickets=async()=>{}; loadPackages=async()=>{}; api=async()=>[];
  navigationReady=true; rememberNavigation(true);`,context);
async function back(){ history.back(); await vm.runInContext('restoreNavigation(window.history.state.itseti)',context); }
async function test(){
  vm.runInContext(`$('device-company').value='10';rememberNavigation();$('device-site').value='101';rememberNavigation();`,context);
  await vm.runInContext(`selectDevice({id:'fixture-pc',inventory:'0553',company:'Fixture'})`,context);
  assert.equal(position,3,'company, object and PC each create one browser entry');
  await back();
  assert.equal(vm.runInContext('state.selectedDevice',context),null,'Back closes the PC card');
  assert.equal(elements.get('device-site').value,'101','Back returns to the same object');
  await back(); assert.equal(elements.get('device-site').value,''); assert.equal(elements.get('device-company').value,'10');
  await back(); assert.equal(elements.get('device-company').value,'','Back returns to the company list');
  vm.runInContext(`showView('monitoring')`,context);
  await vm.runInContext(`selectDevice({id:'fixture-pc',inventory:'0553'})`,context);
  await back(); assert.equal(vm.runInContext('state.view',context),'monitoring','Back from a monitoring PC returns to monitoring');
  assert.equal(vm.runInContext(`monitoringParameters().has('types')`,context),false,'all problem kinds are enabled initially');
  kinds.forEach(item=>item.checked=false);
  assert.equal(vm.runInContext(`monitoringParameters().get('types')`,context),'none','no checkboxes means no problems, not all');
  kinds[0].checked=true;kinds[1].checked=true;
  assert.equal(vm.runInContext(`monitoringParameters().get('types')`,context),'events,cpu');
  vm.runInContext(`rememberNavigation()`,context); const saved=structuredClone(history.state.itseti);
  kinds.forEach(item=>item.checked=true);
  await vm.runInContext(`restoreNavigation(window.history.state.itseti)`,context);
  assert.deepEqual(kinds.filter(item=>item.checked).map(item=>item.dataset.monitorKind),saved.kinds,'Back restores type checkboxes');
  console.log('PASS: browser history for company/object/PC and monitoring, retained filters, all/subset/no problem kinds');
}
test().catch(error=>{console.error(error);process.exitCode=1;});
