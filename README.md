# FusionCoop

Online co-op for **Plants vs. Zombies Fusion 4.0.5**: two to four players defend the same lawn, each from their own PC.
A [MelonLoader](https://github.com/LavaGang/MelonLoader) mod.

[ภาษาไทยด้านล่าง](#ภาษาไทย)

> Unofficial fan mod. PvZ Fusion is made by 蓝飘飘fly; Plants vs. Zombies belongs to PopCap / EA.
> This mod is not affiliated with either.

## Status

- Tested with **2 players on two PCs** over Radmin VPN. Up to 4 players are supported
- Game version **4.0.5 only**. Every player needs the same FusionCoop version (the game tells you if not).

| Mode | Co-op |
|---|---|
| Adventure | Yes |
| Challenge: normal, random-card and conveyor levels | Yes |
| Challenge: Gods (诸神) levels, tower defense evolution | Yes |
| Survival | Yes |
| Odyssey | Yes (the host picks buffs; jigsaw pieces not tested) |
| Puzzle, most mini-games (Bejeweled, 2048, billiards, pots, ...) | Single player only for now ("Co-op in development") |

## Install

You need PvZ Fusion 4.0.5 with MelonLoader **0.7.3** (net6). If the game asks for it, install the
.NET 6 Desktop Runtime (x64).

1. Put `FusionCoop.dll` in the game's `Mods` folder.
2. Start the game. A **Co-op** button appears at the bottom right of the main menu.

## Play

Both PCs need to reach each other: the same LAN, or a VPN such as **Radmin VPN**, ZeroTier or Tailscale.

1. **Host:** Co-op > *Host room*. The window lists this PC's IP addresses; press *Copy* next to the one your
   friend can reach (the Radmin VPN one when you are not on the same LAN) and send it.
2. **Friend:** Co-op > type a name > paste the address in *Host IP* > *Join*.
3. The first time the host opens a room, Windows may ask about the firewall: allow it on **Private and Public**
   networks. FusionCoop uses **TCP port 43096**.

In a room:

- The host picks the level; everyone else follows. Everyone chooses their own cards; the battle starts when all
  players are ready (the host can force it).
- **Tab** opens the players window: who is ready, ping, *Kick* (host), *Show plant owners*, *Tag style*,
  *Force start*, *Leave room*.
- Plant owner tags: P1 / P2 in the bottom-right corner of each planted cell (stone, gold or text only). With
  *Show plant owners* off, only the cell under the mouse shows its tag.
- **Right-click** a cell with nothing in hand to ping it: *Plant here*, *Look here*, *Dig up* or *Danger*
  (*Cancel* in the middle). Everyone sees it on that cell for 3 seconds.
- Every player has their own sun. Dropped items and cards appear on each player's own screen.
- The host's game decides what happens (zombies, damage, wins); other windows follow it.
- Only the host can pause, restart, or change the game speed and difficulty; the other players' pause menus
  show the host's values.
- Tutorial lessons (Adventure 1, 4, 7, ...) are skipped in a room.
- A player who goes back to the main menu stays in the room and can *Rejoin* the running level from the Co-op
  window.
- Co-op > *Rules* lists these rules in the game.

### Magnetar

If the Magnetar mod is installed, its modules that change the game (Level, Tools, Plant, Zombie,
Time Scale, Custom Difficulty) follow the host while in a room; players get their own settings back when they
leave. Visual modules stay each player's own. FusionCoop works without Magnetar.

## Known issues

- A window can close by itself while loading (before FusionCoop starts): start it again.
- 3–4 player rooms and Odyssey jigsaw pieces are untested.

## Settings

`UserData/MelonPreferences.cfg`, section `[FusionCoop]`: `PlayerName`, `LastIp`, `Port` (default 43096),
`OwnerTags`, `OwnerTagStyle`. The others are testing tools and stay off. **F8** shows a debug panel.

## Build

.NET 6 SDK. The project expects the game at `../games/4.0.5` (with MelonLoader's generated
`Il2CppAssemblies`); set `GameDir` to build against another folder:

```
dotnet build -c Release -p:GameDir="C:\Games\PvZ Fusion"
```

The build copies `FusionCoop.dll` into the game's `Mods` folder.

## License

See [LICENSE](LICENSE).

---

## ภาษาไทย

เล่น co-op ออนไลน์ใน **Plants vs. Zombies Fusion 4.0.5** ผู้เล่น 2–4 คนช่วยกันป้องกันสนามเดียวกัน คนละเครื่อง
เป็น mod สำหรับ MelonLoader

> เป็น mod ของแฟนเกม ไม่ใช่ของทางการ PvZ Fusion สร้างโดย 蓝飘飘fly ส่วน Plants vs. Zombies เป็นของ PopCap / EA

### สถานะ

- ทดสอบแล้วกับ **ผู้เล่น 2 คน สองเครื่อง** ผ่าน Radmin VPN รองรับสูงสุด 4 คน
- ใช้กับเกม **4.0.5 เท่านั้น** ทุกคนต้องใช้ FusionCoop เวอร์ชันเดียวกัน (ถ้าไม่ตรงเกมจะแจ้ง)

| โหมด | co-op |
|---|---|
| Adventure | ได้ |
| Challenge: ด่านปกติ ด่านการ์ดสุ่ม สายพาน | ได้ |
| Challenge: ด่าน Gods (诸神) และทาวเวอร์ดีเฟนซ์ 进化 | ได้ |
| Survival | ได้ |
| Odyssey | ได้ (host เลือกบัฟ, jigsaw ยังไม่ได้ทดสอบ) |
| Puzzle และมินิเกมส่วนใหญ่ (Bejeweled, 2048, บิลเลียด, ทุบไห ฯลฯ) | เล่นคนเดียวไปก่อน (ขึ้น "Co-op in development") |

### ติดตั้ง

ต้องมี PvZ Fusion 4.0.5 กับ MelonLoader **0.7.3** (net6) ถ้าเกมขอ ให้ติดตั้ง .NET 6 Desktop Runtime (x64)

1. วาง `FusionCoop.dll` ในโฟลเดอร์ `Mods` ของเกม
2. เปิดเกม จะมีปุ่ม **Co-op** ที่มุมขวาล่างของเมนูหลัก

### เล่น

สองเครื่องต้องเชื่อมถึงกันได้: วง LAN เดียวกัน หรือ VPN เช่น **Radmin VPN**, ZeroTier, Tailscale

1. **host:** Co-op > *Host room* หน้าต่างจะแสดง IP ของเครื่อง กด *Copy* ที่บรรทัดที่เพื่อนเข้าถึงได้
   (ถ้าอยู่คนละบ้านใช้บรรทัด Radmin VPN) แล้วส่งให้เพื่อน
2. **เพื่อน:** Co-op > ใส่ชื่อ > วาง IP ในช่อง *Host IP* > *Join*
3. ครั้งแรกที่ host เปิดห้อง ถ้า Windows ถามเรื่อง firewall ให้อนุญาตทั้ง **Private และ Public**
   (ใช้ **TCP port 43096**)

ระหว่างอยู่ในห้อง:

- host เลือกด่าน คนอื่นเข้าตามเอง ทุกคนเลือกการ์ดของตัวเอง เกมเริ่มเมื่อทุกคนพร้อม (host บังคับเริ่มได้)
- **Tab** เปิดหน้าต่างผู้เล่น: ใครพร้อมแล้ว, ping, *Kick* (host), *Show plant owners*, *Tag style*,
  *Force start*, *Leave room*
- ป้ายเจ้าของพืช: P1 / P2 ที่มุมขวาล่างของช่องที่มีพืช (แบบหิน ทอง หรือตัวหนังสือ) ถ้าปิด *Show plant owners*
  จะขึ้นเฉพาะช่องที่เอาเมาส์ชี้
- **คลิกขวา** ที่ช่องตอนมือว่างเพื่อ ping บอกเพื่อน: *Plant here*, *Look here*, *Dig up* หรือ *Danger*
  (*Cancel* ตรงกลาง) ทุกคนจะเห็นที่ช่องนั้น 3 วินาที
- sun เป็นของใครของมัน ของดรอปและการ์ดดรอปขึ้นบนจอของแต่ละคน
- เกมของ host เป็นตัวตัดสินผล (ซอมบี้ ความเสียหาย ชนะ/แพ้) จออื่นแสดงตาม
- host เท่านั้นที่ pause, restart, ปรับความเร็วเกม และปรับความยากได้ หน้า Pause ของคนอื่นจะแสดงค่าของ host
- ด่านที่มี tutorial (Adventure 1, 4, 7, ...) จะข้าม tutorial เมื่อเล่นในห้อง
- คนที่กลับไปเมนูหลักยังอยู่ในห้อง และกด *Rejoin* ในหน้าต่าง Co-op เพื่อกลับเข้าด่านที่เล่นอยู่ได้
- Co-op > *Rules* ดูกติกาทั้งหมดในเกม

### Magnetar

ถ้าลง Magnetar ไว้ ระหว่างอยู่ในห้อง โมดูลที่เปลี่ยนตัวเกม (Level, Tools, Plant, Zombie, Time Scale,
Custom Difficulty) จะเป็นไปตาม host และได้ค่าของตัวเองคืนเมื่อออกห้อง โมดูล Visual ตั้งเองได้
ไม่ลง Magnetar ก็เล่นได้ปกติ

### ปัญหาที่รู้แล้ว

- หน้าต่างเกมอาจปิดเองตอนกำลังโหลด (ก่อน FusionCoop เริ่ม): เปิดใหม่อีกครั้ง
- ห้อง 3–4 คน และชิ้น jigsaw ใน Odyssey ยังไม่ได้ทดสอบ

### ตั้งค่า

`UserData/MelonPreferences.cfg` หัวข้อ `[FusionCoop]`: `PlayerName`, `LastIp`, `Port` (ค่าเริ่มต้น 43096),
`OwnerTags`, `OwnerTagStyle` ค่าอื่นเป็นเครื่องมือทดสอบ ปล่อยปิดไว้ กด **F8** เปิดแผงดีบัก

### สัญญาอนุญาต

ดู [LICENSE](LICENSE) (MIT)
