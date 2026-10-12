import { open, stat } from "node:fs/promises";
import { isAbsolute, join } from "node:path";

type Mod = { id: string; name: string; version: string };
type Data = Record<string, unknown>;
const record = (value: unknown): Data => value && typeof value === "object" && !Array.isArray(value) ? value as Data : {};
const list = (value: unknown): unknown[] => Array.isArray(value) ? value : [];
const text = (value: unknown, limit = 2000) => typeof value === "string" ? redact(value).slice(0, limit) : "";

// 日志可能含登录身份/令牌；仅返回警告摘要，敏感整行不回传。
export function redact(value: string): string {
  return value.replace(/\b\d{17}\b/g, "[account-redacted]")
    .replace(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/gi, "[email-redacted]")
    .replace(/\b(?:Bearer\s+\S+|eyJ[\w-]+\.[\w-]+\.[\w-]+)\b/gi, "[token-redacted]")
    .replace(/\b(token|password|secret|authorization|api[_-]?key)\s*[:=]\s*(?:"[^"]*"|'[^']*'|\S+)/gi, "$1=[redacted]");
}

export function warningLines(log: string, limit: number): string[] {
  return log.split(/\r?\n/).filter(line => (/\[(WARN(?:ING)?|ERROR|FATAL)\b/i.test(line) || /^Not serializable type:/.test(line)
    || /^[A-Za-z_][\w.]*Exception:/.test(line))
    && !/\b(login|logged in|useridentifier|userId|password|token|authorization|secret|api[_-]?key)\b/i.test(line))
    .slice(-limit).map(line => redact(line).slice(0, 1200));
}

export function parseGameVersion(playerLog: string): string | null {
  return playerLog.match(/^Game version:\s*(\d+\.\d+\.\d+f\d+)\b/im)?.[1] ?? null;
}

export function parseModdingLog(log: string) {
  const start = log.lastIndexOf("======= Active Playset =======");
  if(start < 0) return { valid: false, playset: null, mods: [] as Mod[], loadedAssemblies: [] as string[] };
  const section = log.slice(start);
  const playsetMatch = section.match(/Active Playset =======\s*\n\s*(.+?)\s*\((\d+)\)/);
  const enabled = section.split("======= Enabled Mods =======")[1]?.split(/\n\[/)[0] ?? "";
  const mods = Array.from(enabled.matchAll(/^\s*- (.+) v([^\r\n]+?) \((\d+)\)\s*$/gm), match =>
    ({ name: text(match[1], 200), version: text(match[2], 80), id: match[3] }));
  const loadedAssemblies = Array.from(new Set(Array.from(section.matchAll(/\[INFO\]\s+Loaded ([^,\r\n]+), Version=/g), m => text(m[1], 200))));
  return { valid: !!playsetMatch && section.includes("Mods initialized in"),
    playset: playsetMatch ? { name: text(playsetMatch[1], 200), id: playsetMatch[2] } : null, mods, loadedAssemblies };
}

// 枚举语义对应 Skyve 1.3.1 的 Skyve.Compatibility.dll；未知值保留，不猜。
const stability: Record<number, string> = {0:"NotReviewed",1:"Stable",2:"NotEnoughInformation",3:"HasIssues",4:"Broken",
  5:"BrokenFromPatch",6:"StableNoNewFeatures",7:"StableNoFutureUpdates",8:"HasIssuesNoFutureUpdates",9:"BreaksOnPatch",
  10:"BrokenFromNewVersion",11:"NumerousReports",12:"Obsolete",13:"CautionWhenUsing",99:"Incompatible"};
const interaction: Record<number, string> = {0:"None",1:"Successor",2:"Alternative",3:"SameFunctionality",4:"IncompatibleWith",
  5:"RequirementAlternative",6:"CausesIssuesWith",7:"RequiredPackages",8:"OptionalPackages",9:"LoadAfter"};
const status: Record<number,string> = {0:"None",1:"Deprecated",2:"Reupload",3:"CausesIssues",4:"SavesCantLoadWithoutIt",
  5:"TestVersion",6:"DependencyMod",7:"SourceCodeNotAvailable",8:"MusicCanBeCopyrighted",9:"IncompleteDescription",
  10:"SourceAvailable",11:"StandardMod",1000:"MissingDlc",1001:"Succeeded",1002:"AutoDeprecated"};

export function summarizeCompatibility(cache: unknown, mods: Mod[], observedGameVersion: string | null) {
  const data = record(cache);
  if(!Array.isArray(data.Packages)) throw new Error("Skyve compatibility cache has an unsupported schema (Packages must be an array).");
  const enabled = new Set(mods.map(mod => mod.id));
  const packages = new Map(list(data.Packages).map(p => [String(record(p).Id), record(p)]));
  return mods.map(mod => {
    const p = packages.get(mod.id);
    if(!p) return { ...mod, reviewed: false, cacheEntryMissing: true };
    return { ...mod, reviewed: true, stabilityRaw: p.Stability, stability: stability[Number(p.Stability)] ?? "Unknown",
      reviewedGameVersion: text(p.ReviewedGameVersion,80), reviewDate: text(p.ReviewDate,80),
      reviewedForObservedVersion: observedGameVersion ? p.ReviewedGameVersion === observedGameVersion : null,
      note: text(p.Note), statuses: list(p.Statuses).map(s => {
        const d=record(s); return {typeRaw:d.Type,type:status[Number(d.Type)] ?? "Unknown", note:text(d.Note)};
      }), interactions: list(p.Interactions).map(i => {
        const d=record(i); return {typeRaw:d.Type, type:interaction[Number(d.Type)] ?? "Unknown", note:text(d.Note),
          packages:list(d.Packages).map(item => {const pkg=record(item);return {id:String(pkg.Id),name:text(pkg.Name,200),
            isDlc:pkg.IsDlc === true, enabled:pkg.IsDlc === true ? null : enabled.has(String(pkg.Id)),
            missingRequired: Number(d.Type)===7 && pkg.IsDlc !== true && !enabled.has(String(pkg.Id))};})};
      }) };
  });
}

async function boundedRead(path: string, limit: number, mode: "full" | "head" | "tail" = "full") {
  const handle = await open(path, "r");
  try {
    const info = await handle.stat();
    if(!info.isFile()) throw new Error("Not a regular diagnostic file.");
    if(info.size > limit && mode === "full") throw new Error(`Diagnostic file exceeds the ${limit}-byte limit.`);
    const size = Math.min(info.size,limit), buffer = Buffer.alloc(size), position=mode === "tail" ? info.size-size : 0;
    const {bytesRead}=await handle.read(buffer,0,size,position);
    let content=buffer.subarray(0,bytesRead).toString("utf8");
    if(position>0) content=content.slice(content.indexOf("\n")+1);
    if(mode === "head" && info.size > bytesRead) content=content.slice(0,Math.max(0,content.lastIndexOf("\n")));
    return {content,modifiedAt:info.mtime.toISOString(),bytes:info.size,truncated:info.size>bytesRead,readMode:mode};
  } finally {await handle.close();}
}

export async function readSkyveStatus(includeLogs: boolean, logLimit: number, root = process.env.CS2_GAME_USER_DATA
  ?? (process.env.LOCALAPPDATA ? join(process.env.LOCALAPPDATA,"..","LocalLow","Colossal Order","Cities Skylines II") : "")) {
  if(!root || !isAbsolute(root)) throw new Error("CS2_GAME_USER_DATA must identify the existing absolute game user-data directory.");
  const errors: {source:string;error:string}[] = [], sources: Record<string,unknown> = {};
  const read = async (name:string,path:string,limit:number,mode: "full" | "head" | "tail" = "full") => {
    try { const file=await boundedRead(path,limit,mode);sources[name]={modifiedAt:file.modifiedAt,bytes:file.bytes,truncated:file.truncated,readMode:file.readMode};return file.content; }
    catch(error) {errors.push({source:name,error:error instanceof Error ? redact(error.message) : "Read failed"});return null;}
  };
  const modLog=await read("Modding.log",join(root,"Logs","Modding.log"),2*1024*1024);
  const observed=modLog===null ? null : parseModdingLog(modLog);
  const playerHead=await read("Player.log version",join(root,"Player.log"),64*1024,"head");
  const gameVersion=playerHead===null ? null : parseGameVersion(playerHead);
  const cache=await read("CompatibilityDataCache.json",join(root,"ModsData","Skyve","CompatibilityDataCache.json"),8*1024*1024);
  let compatibility: unknown=null;
  if(cache!==null) try {compatibility=summarizeCompatibility(JSON.parse(cache),observed?.valid ? observed.mods : [],gameVersion);}
  catch(error) {errors.push({source:"CompatibilityDataCache.json",error:error instanceof Error ? error.message : "Invalid cache"});}
  const install=await read("Skyve InstallPath",join(root,"ModsSettings","Skyve","InstallPath"),8192);
  let installed=false;
  if(install?.trim() && isAbsolute(install.trim())) try {installed=(await stat(join(install.trim(),"Skyve.exe"))).isFile();} catch {}
  const logs:Record<string,unknown>={};
  if(includeLogs) for(const [name,path] of [
    ["SkyveMod.log",join(root,"Logs","SkyveMod.log")],
    ["SkyveApp.log",join(root,"ModsData","Skyve","Logs","SkyveApp.log")],
    ["SkyveApp_Previous.log",join(root,"ModsData","Skyve","Logs","SkyveApp_Previous.log")],
    ["SceneFlow.log",join(root,"Logs","SceneFlow.log")],
  ]) {const log=await read(name,path,256*1024,"tail");if(log!==null) logs[name]=warningLines(log,logLimit);}
  if(includeLogs && modLog!==null) logs["Modding.log"]=warningLines(modLog,logLimit);
  if(includeLogs) {
    const playerTail=await read("Player.log warnings",join(root,"Player.log"),512*1024,"tail");
    if(playerTail!==null) logs["Player.log"]=warningLines(playerTail,logLimit);
  }
  return {capturedAtUtc:new Date().toISOString(),readOnly:true,skyveInstalled:installed,observedGameVersion:gameVersion,
    sources,observed,compatibility,logs,errors,
    note:"启用模组来自最近一次原生 Modding.log，并非完整订阅清单；游戏退出后日志仍留存，实时加载须核对 runtime。版本从Player头部64KiB读取，警告从尾部512KiB读取；SceneFlow尾部256KiB另列启动错误，不扫描巨大日志，诊断摘要不等于根因或就绪证明。兼容性为 Skyve 缓存及审查记录，不保证当前运行无错误。可选搭配缺失不是必需依赖错误；旧审查版本不自动判为损坏。枚举依据 Skyve 1.3.1；不读取账号缓存、认证数据，也不执行 Skyve 命令管道。"};
}
