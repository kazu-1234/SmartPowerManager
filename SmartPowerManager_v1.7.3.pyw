# version: 1.7.2
# -*- coding: utf-8 -*-
"""
SmartPowerManager v1.7.2
PCのシャットダウンスケジュール管理アプリケーション

機能:
- 毎日/毎週/一回限りのシャットダウン/再起動スケジュール
- 毎週スケジュール: 各曜日ごとに個別設定可能
- x時間後シャットダウン: 1,3,6,9,12時間後を選択可能（削除可能）
- 優先順位: 一回限り > 毎週 > 毎日
- 自動起動タブ: MACアドレス表示（Pico W用）
- シャットダウン/再起動前確認ダイアログ（60秒カウントダウン）
- シャットダウンと再起動の排他制御（同時刻不可）

v1.6.3 変更点:
- Pico W連携強化: 設定の読込機能(Fetch)を追加、JSON形式での同期に対応
- Web UI: 毎日スケジュール設定フォームの追加
"""

import os
import sys
import json
import uuid
import subprocess
import tkinter as tk
from tkinter import ttk, messagebox
from datetime import datetime, timedelta
import threading
import time
import urllib.request
import urllib.error
import winreg
import pystray
from PIL import Image, ImageDraw

try:
    import ctypes
    hwnd = ctypes.windll.kernel32.GetConsoleWindow()
    if hwnd != 0:
        ctypes.windll.user32.ShowWindow(hwnd, 0)
except Exception:
    pass

# --- 高DPI対応 (Windows向け) ---
try:
    from ctypes import windll
    windll.shcore.SetProcessDpiAwareness(1)
except Exception:
    pass


# =============================================================================
# ディスプレイ制御ヘルパー関数
# =============================================================================
def wake_display():
    """ディスプレイを起動する（画面オフ状態から復帰）"""
    try:
        # ディスプレイを一時的に起動（ES_CONTINUOUSなしで一回限り）
        ctypes.windll.kernel32.SetThreadExecutionState(0x00000003)  # ES_DISPLAY_REQUIRED | ES_SYSTEM_REQUIRED
    except Exception:
        pass

def reset_power_state():
    """電源管理をシステムデフォルトにリセット（スリープ/画面オフを許可）"""
    try:
        # すべての電源管理設定をクリアしてシステム設定に従う
        ctypes.windll.kernel32.SetThreadExecutionState(0x80000000)  # ES_CONTINUOUS
    except Exception:
        pass


# =============================================================================
# 定数定義
# =============================================================================
APP_VERSION = "v1.7.3"
APP_TITLE = "SmartPowerManager"

# 設定ファイルのパス決定（PyInstaller対応）
if getattr(sys, 'frozen', False):
    BASE_DIR = os.path.dirname(sys.executable)
else:
    BASE_DIR = os.path.dirname(os.path.abspath(__file__))

CONFIG_FILE = os.path.join(BASE_DIR, "schedules.json")

# GitHubのリポジトリ情報
GITHUB_USER = "kazu-1234"
GITHUB_REPO = "SmartPowerManager"
GITHUB_API_URL = f"https://api.github.com/repos/{GITHUB_USER}/{GITHUB_REPO}/releases/latest"

WEEKDAYS_JP = ["月曜日", "火曜日", "水曜日", "木曜日", "金曜日", "土曜日", "日曜日"]
WEEKDAYS_SHORT = ["月", "火", "水", "木", "金", "土", "日"]
HOURS_LATER_OPTIONS = [1, 3, 6, 9, 12]

ACTION_SHUTDOWN = "shutdown"
ACTION_RESTART = "restart"

# =============================================================================
# MACアドレス取得関数
# =============================================================================
def get_mac_addresses():
    """PCのMACアドレスを取得"""
    mac_list = []
    try:
        import csv
        import io
        result = subprocess.run(
            ["getmac", "/v", "/fo", "csv"],
            capture_output=True, text=True, check=True,
            creationflags=subprocess.CREATE_NO_WINDOW,
            encoding='cp932'
        )
        reader = csv.reader(io.StringIO(result.stdout))
        header = next(reader, None)
        for row in reader:
            if len(row) >= 3:
                adapter_name = row[1].strip()
                mac = row[2].strip()
                if mac and mac != "N/A":
                    mac = mac.replace('-', ':')
                    mac_list.append({"name": adapter_name, "mac": mac})
    except Exception:
        pass
    
    if not mac_list:
        try:
            import uuid as uuid_lib
            mac_int = uuid_lib.getnode()
            mac_bytes = [(mac_int >> (8 * i)) & 0xff for i in range(6)][::-1]
            mac_str = ':'.join(f'{b:02X}' for b in mac_bytes)
            mac_list.append({"name": "Default Interface", "mac": mac_str})
        except Exception:
            pass
    
    return mac_list

# =============================================================================
# PyInstaller用リソースパス取得関数
# =============================================================================
def resource_path(relative_path):
    try:
        base_path = sys._MEIPASS
    except Exception:
        base_path = os.path.abspath(".")
    return os.path.join(base_path, relative_path)


# =============================================================================
# スケジュール管理クラス
# =============================================================================
class ScheduleManager:
    """シャットダウンスケジュールを管理するクラス"""
    
    def __init__(self, config_path=CONFIG_FILE):
        self.config_path = config_path
        self.lock = threading.Lock()
        # daily: { "shutdown": {enabled, h, m}, "restart": {enabled, h, m} }
        self.daily_schedule = {
            ACTION_SHUTDOWN: {"enabled": False, "hour": 23, "minute": 0},
            ACTION_RESTART:  {"enabled": False, "hour": 23, "minute": 0}
        }
        self.weekly_schedules = []
        self.onetime_schedules = []
        self.load_failed = False # ロード失敗フラグ
        
        # v1.6.0: Raspberry Pi / Startup Settings
        self.pico_settings = {
            "ip": "192.168.10.x",
            "target_mac": "",
            "startup_daily": {"enabled": False, "hour": 7, "minute": 0},
            "startup_weekly": [],
            "startup_onetime": []
        }
        
        self.debug_mode = False
        self.disclaimer_accepted = False
        self.skipped_dates = []  # キャンセルされたスケジュール日時のリスト
        self.load()
    
    def load(self):
        """設定ファイルから読み込み"""
        if os.path.exists(self.config_path):
            with self.lock:
                try:
                    with open(self.config_path, 'r', encoding='utf-8') as f:
                        data = json.load(f)
                        
                        # dailyスケジュールの読み込みと移行
                        loaded_daily = data.get("daily", {})
                        if "enabled" in loaded_daily:
                            # 旧形式
                            self.daily_schedule[ACTION_SHUTDOWN] = loaded_daily
                        else:
                            # 新形式
                            if ACTION_SHUTDOWN in loaded_daily:
                                self.daily_schedule[ACTION_SHUTDOWN] = loaded_daily[ACTION_SHUTDOWN]
                            if ACTION_RESTART in loaded_daily:
                                self.daily_schedule[ACTION_RESTART] = loaded_daily[ACTION_RESTART]
                        
                        # 毎週/一回限りの読み込み
                        raw_weekly = []
                        if "weekly" in data and isinstance(data["weekly"], dict):
                            old_weekly = data["weekly"]
                            if old_weekly.get("enabled", False):
                                raw_weekly = [{
                                    "id": str(uuid.uuid4()),
                                    "weekday": old_weekly.get("weekday", 0),
                                    "hour": old_weekly.get("hour", 23),
                                    "minute": old_weekly.get("minute", 0),
                                    "action": ACTION_SHUTDOWN
                                }]
                        else:
                            raw_weekly = data.get("weekly_schedules", [])
                        
                        self.weekly_schedules = []
                        for item in raw_weekly:
                            if "action" not in item:
                                item["action"] = ACTION_SHUTDOWN
                            self.weekly_schedules.append(item)
                            
                        raw_onetime = data.get("onetime", [])
                        self.onetime_schedules = []
                        for item in raw_onetime:
                            if "action" not in item:
                                item["action"] = ACTION_SHUTDOWN
                            self.onetime_schedules.append(item)

                        # v1.6.0 load
                        self.pico_settings = data.get("pico_settings", self.pico_settings)

                        self.debug_mode = data.get("debug_mode", False)
                        self.disclaimer_accepted = data.get("disclaimer_accepted", False)
                        self.skipped_dates = data.get("skipped_dates", [])
                except Exception as e:
                    print(f"設定の読み込みに失敗: {e}")
                    # ロード失敗時はフラグを立てて、安易な上書きを防ぐ
                    self.load_failed = True
    
    def save(self):
        """設定ファイルに保存"""
        if self.load_failed:
             print("ロード失敗状態のため保存をスキップします")
             return

        data = {
            "daily": self.daily_schedule,
            "weekly_schedules": self.weekly_schedules,
            "onetime": self.onetime_schedules,
            "pico_settings": self.pico_settings,
            "debug_mode": self.debug_mode,
            "disclaimer_accepted": self.disclaimer_accepted,
            "skipped_dates": self.skipped_dates
        }
        with self.lock:
            try:
                with open(self.config_path, 'w', encoding='utf-8') as f:
                    json.dump(data, f, ensure_ascii=False, indent=2)
            except Exception as e:
                print(f"設定の保存に失敗: {e}")
            
    def check_conflict(self, action_type, schedule_type, time_info):
        """同一日時に他方のアクションが登録されていないか確認（競合する場合はメッセージを返す）"""
        other_action = ACTION_RESTART if action_type == ACTION_SHUTDOWN else ACTION_SHUTDOWN
        other_label = "再起動" if other_action == ACTION_RESTART else "シャットダウン"
        
        if schedule_type == "daily":
            other_daily = self.daily_schedule[other_action]
            if other_daily["enabled"]:
                if (other_daily["hour"] == time_info["hour"] and 
                    other_daily["minute"] == time_info["minute"]):
                    return f"毎日 ({other_daily['hour']:02d}:{other_daily['minute']:02d}) の{other_label}"
        elif schedule_type == "weekly":
            for s in self.weekly_schedules:
                if s["action"] == other_action:
                    if (s["weekday"] == time_info["weekday"] and
                        s["hour"] == time_info["hour"] and
                        s["minute"] == time_info["minute"]):
                        return f"毎週 {WEEKDAYS_JP[s['weekday']]} {s['hour']:02d}:{s['minute']:02d} の{other_label}"
            other_daily = self.daily_schedule[other_action]
            if other_daily["enabled"]:
                if (other_daily["hour"] == time_info["hour"] and
                    other_daily["minute"] == time_info["minute"]):
                    return f"毎日 ({other_daily['hour']:02d}:{other_daily['minute']:02d}) の{other_label}"
        elif schedule_type == "onetime":
            target_dt = datetime.strptime(time_info["datetime"], "%Y-%m-%d %H:%M")
            for s in self.onetime_schedules:
                if s["action"] == other_action and not s.get("executed", False):
                    try:
                        s_dt = datetime.strptime(s["datetime"], "%Y-%m-%d %H:%M")
                        if s_dt == target_dt:
                            return f"一回限り {s['datetime']} の{other_label}"
                    except: pass
            target_weekday = target_dt.weekday()
            for s in self.weekly_schedules:
                if s["action"] == other_action:
                    if (s["weekday"] == target_weekday and
                        s["hour"] == target_dt.hour and
                        s["minute"] == target_dt.minute):
                        return f"毎週 {WEEKDAYS_JP[s['weekday']]} {s['hour']:02d}:{s['minute']:02d} の{other_label}"
            other_daily = self.daily_schedule[other_action]
            if other_daily["enabled"]:
                if (other_daily["hour"] == target_dt.hour and
                    other_daily["minute"] == target_dt.minute):
                    return f"毎日 ({other_daily['hour']:02d}:{other_daily['minute']:02d}) の{other_label}"
        return None

    def add_weekly(self, action, weekday, hour, minute):
        schedule = {"id": str(uuid.uuid4()), "action": action, "weekday": weekday, "hour": hour, "minute": minute}
        self.weekly_schedules.append(schedule)
        self.save()
        return schedule["id"]
    
    def remove_weekly(self, schedule_id):
        self.weekly_schedules = [s for s in self.weekly_schedules if s["id"] != schedule_id]
        self.save()
    
    def add_onetime(self, action, dt_str, source="manual"):
        schedule = {"id": str(uuid.uuid4()), "action": action, "datetime": dt_str, "executed": False, "source": source}
        self.onetime_schedules.append(schedule)
        self.save()
        return schedule["id"]
    
    def add_onetime_hours_later(self, action, hours):
        target_time = datetime.now() + timedelta(hours=hours)
        dt_str = target_time.strftime("%Y-%m-%d %H:%M")
        return self.add_onetime(action, dt_str, source="quick")
    
    def remove_onetime(self, schedule_id):
        self.onetime_schedules = [s for s in self.onetime_schedules if s["id"] != schedule_id]
        self.save()
    
    def clear_executed_onetime(self, action):
        self.onetime_schedules = [s for s in self.onetime_schedules 
                                 if not (s["action"] == action and s.get("executed", False))]
        self.save()
    
    # 互換性のため残すが、実際にはget_next_eventを使用推奨
    def get_next_shutdown_info(self):
        return self.get_next_event_info()

    def get_next_event_info(self):
        now = datetime.now()
        candidates = []
        
        for s in self.onetime_schedules:
            if s.get("executed", False): continue
            try:
                dt = datetime.strptime(s["datetime"], "%Y-%m-%d %H:%M")
                if dt > now:
                    # スキップされた日時をチェック
                    dt_str = dt.strftime("%Y-%m-%d %H:%M")
                    if dt_str not in self.skipped_dates:
                        candidates.append((dt, "onetime", s["action"], s["id"]))
            except: pass
        
        for s in self.weekly_schedules:
            target_weekday = s["weekday"]
            target_time = now.replace(hour=s["hour"], minute=s["minute"], second=0, microsecond=0)
            days_ahead = target_weekday - now.weekday()
            if days_ahead < 0 or (days_ahead == 0 and target_time <= now):
                days_ahead += 7
            next_weekly = target_time + timedelta(days=days_ahead)
            
            # Find next valid occurrence (skip skipped_dates)
            for _ in range(5):
                dt_str = next_weekly.strftime("%Y-%m-%d %H:%M")
                if dt_str not in self.skipped_dates:
                    candidates.append((next_weekly, "weekly", s["action"], s["id"]))
                    break
                next_weekly += timedelta(days=7)
        
        for action in [ACTION_SHUTDOWN, ACTION_RESTART]:
            setting = self.daily_schedule[action]
            if setting["enabled"]:
                target_time = now.replace(hour=setting["hour"], minute=setting["minute"], second=0, microsecond=0)
                if target_time <= now: target_time += timedelta(days=1)
                
                # Find next valid occurrence
                for _ in range(5):
                    dt_str = target_time.strftime("%Y-%m-%d %H:%M")
                    if dt_str not in self.skipped_dates:
                        candidates.append((target_time, "daily", action, None))
                        break
                    target_time += timedelta(days=1)
        
        if not candidates: return None, None, None, None
        candidates.sort(key=lambda x: x[0])
        return candidates[0] # (dt, type, action, id)
    
    def check_and_execute(self, log_callback=None):
        now = datetime.now()
        current_weekday = now.weekday()
        
        # 一回限り
        for s in self.onetime_schedules:
            if s.get("executed", False): continue
            try:
                scheduled_dt = datetime.strptime(s["datetime"], "%Y-%m-%d %H:%M")
                if abs((now - scheduled_dt).total_seconds()) < 60:
                    s["executed"] = True
                    self.save()
                    self._execute_action(s["action"], f"一回限り ({s['datetime']})", log_callback)
                    return True
            except: pass
        
        # 毎週
        for s in self.weekly_schedules:
            if (current_weekday == s["weekday"] and now.hour == s["hour"] and now.minute == s["minute"]):
                dt_str = now.strftime("%Y-%m-%d %H:%M")
                if dt_str in self.skipped_dates:
                     if log_callback: log_callback(f"スキップされたスケジュール: 毎週 {dt_str}")
                     return False
                self._execute_action(s["action"], f"毎週 ({WEEKDAYS_JP[s['weekday']]} {s['hour']:02d}:{s['minute']:02d})", log_callback)
                return True
        
        # 毎日
        for action, setting in self.daily_schedule.items():
            if setting["enabled"]:
                if now.hour == setting["hour"] and now.minute == setting["minute"]:
                    dt_str = now.strftime("%Y-%m-%d %H:%M")
                    if dt_str in self.skipped_dates:
                         if log_callback: log_callback(f"スキップされたスケジュール: 毎日 {dt_str}")
                         return False 
                    self._execute_action(action, f"毎日 ({setting['hour']:02d}:{setting['minute']:02d})", log_callback)
                    return True
        return False
    
    def _execute_action(self, action, trigger_type, log_callback=None):
        label = "シャットダウン" if action == ACTION_SHUTDOWN else "再起動"
        msg = f"[{datetime.now().strftime('%Y-%m-%d %H:%M:%S')}] {label}予定: {trigger_type}"
        if log_callback: log_callback(msg)
        if self.debug_mode:
            if log_callback: log_callback(f"[デバッグモード] 実際の{label}はスキップされました")
            return
        
        self._pending_active = True
        self._pending_action = action
        self._pending_trigger = trigger_type
        self._pending_callback = log_callback
        # 互換性用（古いコードが参照している場合のため）
        if action == ACTION_SHUTDOWN:
            self._pending_shutdown = True 
            self._pending_trigger_type = trigger_type
    
    def _do_immediate_action(self, action, log_callback=None):
        label = "シャットダウン" if action == ACTION_SHUTDOWN else "再起動"
        flag = "/s" if action == ACTION_SHUTDOWN else "/r"
        try:
            subprocess.Popen(["shutdown", flag, "/t", "0"], creationflags=subprocess.CREATE_NO_WINDOW)
            if log_callback: log_callback(f"{label}コマンドを送信しました")
        except Exception as e:
            if log_callback: log_callback(f"{label}コマンド実行エラー: {e}")
    def add_startup_weekly(self, weekday, hour, minute):
        schedule = {"id": str(uuid.uuid4()), "weekday": weekday, "hour": hour, "minute": minute, "enabled": True}
        self.pico_settings["startup_weekly"].append(schedule)
        self.save()
        return schedule["id"]
    
    def remove_startup_weekly(self, schedule_id):
        self.pico_settings["startup_weekly"] = [s for s in self.pico_settings["startup_weekly"] if s["id"] != schedule_id]
        self.save()

    def add_startup_onetime(self, dt_str, source="manual"):
        schedule = {"id": str(uuid.uuid4()), "datetime": dt_str, "enabled": True, "source": source}
        self.pico_settings["startup_onetime"].append(schedule)
        self.save()
        return schedule["id"]

    def add_startup_onetime_hours_later(self, hours):
        target_time = datetime.now() + timedelta(hours=hours)
        dt_str = target_time.strftime("%Y-%m-%d %H:%M")
        return self.add_startup_onetime(dt_str, source="quick")

    def remove_startup_onetime(self, schedule_id):
        self.pico_settings["startup_onetime"] = [s for s in self.pico_settings["startup_onetime"] if s["id"] != schedule_id]
        self.save()

    # =============================================================================
    # メインGUIアプリケーション
# =============================================================================
class SmartPowerManagerApp(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title(APP_TITLE)
        self.geometry("900x650")
        self.minsize(900, 650)
        self.resizable(False, False)
        
        # アイコン設定
        try:
            icon_path = resource_path("app_icon.ico")
            if os.path.exists(icon_path):
                self.iconbitmap(icon_path)
        except: pass

        self.schedule_manager = ScheduleManager()
        self._check_disclaimer()
        
        try:
            self._setup_styles()
        except: pass
        
        self.monitor_running = False
        self.monitor_thread = None
        
        self._setup_widgets()
        self._setup_realtime_clock() # 追加: リアルタイム時計機能
        self._update_schedule_display()
        self._start_monitor()
        self.protocol("WM_DELETE_WINDOW", self._on_close)
        
        # スタートアップフォルダのショートカット削除
        self._clean_manual_startup()
        # 古いアップデート残骸の削除
        self._clean_old_updates()
        # レガシーな更新バッチが残っていたら削除（無限ループ防止）
        self._cleanup_legacy_bat()
        
        # 既存のスタートアップ設定をチェックして更新
        self._ensure_startup_arg()

        # スタートアップ起動判定
        if "--startup" in sys.argv:
            self.withdraw()
            
        threading.Thread(target=self._run_tray, daemon=True).start()
    
    def _setup_widgets(self):
        main_frame = ttk.Frame(self, padding="10")
        main_frame.pack(fill=tk.BOTH, expand=True)
        
        notebook = ttk.Notebook(main_frame)
        notebook.pack(fill=tk.BOTH, expand=True)
        
        # シャットダウンタブ
        self.shutdown_tab = ttk.Frame(notebook, padding="10")
        notebook.add(self.shutdown_tab, text="シャットダウン")
        self._setup_shutdown_tab()
        
        # 再起動タブ（追加）
        self.restart_tab = ttk.Frame(notebook, padding="10")
        notebook.add(self.restart_tab, text="再起動")
        self._setup_restart_tab()
        
        # 起動タブ (v1.6.0: Auto Boot -> Startup)
        self.startup_tab = ttk.Frame(notebook, padding="10")
        notebook.add(self.startup_tab, text="起動")
        self._setup_startup_tab()
        
        # 設定タブ
        self.settings_tab = ttk.Frame(notebook, padding="10")
        notebook.add(self.settings_tab, text="設定")
        self._setup_settings_tab()
        
        # アップデートタブ
        self.update_tab = ttk.Frame(notebook, padding="10")
        notebook.add(self.update_tab, text="アップデート")
        self._setup_update_tab()
    
    def _setup_shutdown_tab(self):
        # v1.4.3のコードをベースに、変数のみクラスメンバとして保持する形にする
        # ただしv1.4.3はself.daily_hour_varなどを使っていたので、そのまま使う
        
        columns_frame = ttk.Frame(self.shutdown_tab)
        columns_frame.pack(fill=tk.BOTH, expand=True)
        columns_frame.columnconfigure(0, weight=1, uniform="col")
        columns_frame.columnconfigure(1, weight=1, uniform="col")
        columns_frame.rowconfigure(0, weight=1)
        
        left_frame = ttk.Frame(columns_frame)
        left_frame.grid(row=0, column=0, sticky="nsew", padx=(0, 5))
        left_frame.rowconfigure(2, weight=1)
        
        quick_frame = ttk.LabelFrame(left_frame, text="クイック設定：x時間後", padding="8")
        quick_frame.pack(fill=tk.X, pady=3)
        
        quick_btn_row = ttk.Frame(quick_frame)
        quick_btn_row.pack(fill=tk.X)
        for hours in HOURS_LATER_OPTIONS:
            btn = ttk.Button(quick_btn_row, text=f"{hours}h", width=4,
                           command=lambda h=hours: self._add_hours_later(h))
            btn.pack(side=tk.LEFT, padx=2, pady=2)
        
        quick_list_frame = ttk.Frame(quick_frame)
        quick_list_frame.pack(fill=tk.BOTH, expand=True, pady=(5, 0))
        
        self.quick_tree = ttk.Treeview(quick_list_frame, columns=("datetime",), show="headings", height=3)
        self.quick_tree.heading("datetime", text="予定日時")
        self.quick_tree.column("datetime", width=140)
        self.quick_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        quick_scroll = ttk.Scrollbar(quick_list_frame, orient=tk.VERTICAL, command=self.quick_tree.yview)
        self.quick_tree.configure(yscrollcommand=quick_scroll.set)
        quick_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        ttk.Button(quick_frame, text="選択を削除", command=self._remove_selected_quick).pack(anchor=tk.W, pady=(3, 0))
        
        daily_frame = ttk.LabelFrame(left_frame, text="毎日スケジュール", padding="8")
        daily_frame.pack(fill=tk.X, pady=3)
        daily_row = ttk.Frame(daily_frame)
        daily_row.pack(fill=tk.X)
        
        self.daily_enabled_var = tk.BooleanVar(value=self.schedule_manager.daily_schedule[ACTION_SHUTDOWN]["enabled"])
        ttk.Checkbutton(daily_row, text="有効", variable=self.daily_enabled_var, command=self._on_daily_changed).pack(side=tk.LEFT)
        ttk.Label(daily_row, text="時刻:").pack(side=tk.LEFT, padx=(15, 5))
        self.daily_hour_var = tk.StringVar(value=f"{self.schedule_manager.daily_schedule[ACTION_SHUTDOWN]['hour']:02d}")
        sb_h = ttk.Spinbox(daily_row, from_=0, to=23, width=3, textvariable=self.daily_hour_var, format="%02.0f", wrap=True, command=self._on_daily_changed)
        sb_h.pack(side=tk.LEFT)
        self._bind_realtime_stop("daily_s_h", sb_h) # 追尾停止バインド
        ttk.Label(daily_row, text=":").pack(side=tk.LEFT)
        self.daily_minute_var = tk.StringVar(value=f"{self.schedule_manager.daily_schedule[ACTION_SHUTDOWN]['minute']:02d}")
        sb_m = ttk.Spinbox(daily_row, from_=0, to=59, width=3, textvariable=self.daily_minute_var, format="%02.0f", wrap=True, command=self._on_daily_changed)
        sb_m.pack(side=tk.LEFT)
        self._bind_realtime_stop("daily_s_m", sb_m)
        
        next_left_frame = ttk.LabelFrame(left_frame, text="次回スケジュール実行予定", padding="8")
        next_left_frame.pack(fill=tk.X, pady=3)
        next_row = ttk.Frame(next_left_frame)
        next_row.pack(fill=tk.X)
        self.next_shutdown_var = tk.StringVar(value="スケジュールなし")
        ttk.Label(next_row, textvariable=self.next_shutdown_var).pack(side=tk.LEFT, anchor=tk.W)
        ttk.Button(next_row, text="キャンセル", command=self._cancel_all).pack(side=tk.RIGHT, padx=(10, 0))
        
        log_frame = ttk.LabelFrame(left_frame, text="ログ", padding="5")
        log_frame.pack(fill=tk.BOTH, expand=True, pady=3)
        self.log_text = tk.Text(log_frame, height=8, state="disabled", wrap="word")
        log_scroll = ttk.Scrollbar(log_frame, orient=tk.VERTICAL, command=self.log_text.yview)
        self.log_text.config(yscrollcommand=log_scroll.set)
        self.log_text.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        log_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        
        right_frame = ttk.Frame(columns_frame)
        right_frame.grid(row=0, column=1, sticky="nsew", padx=(5, 0))
        right_frame.rowconfigure(0, weight=1)
        right_frame.rowconfigure(1, weight=1)
        
        weekly_frame = ttk.LabelFrame(right_frame, text="毎週スケジュール", padding="8")
        weekly_frame.pack(fill=tk.BOTH, expand=True, pady=3)
        weekly_add_row = ttk.Frame(weekly_frame)
        weekly_add_row.pack(fill=tk.X, pady=(0, 5))
        self.weekly_add_day_var = tk.StringVar(value=WEEKDAYS_JP[0])
        ttk.Combobox(weekly_add_row, textvariable=self.weekly_add_day_var, values=WEEKDAYS_JP, width=7, state="readonly").pack(side=tk.LEFT, padx=2)
        self.weekly_add_hour_var = tk.StringVar(value="23")
        sb_wh = ttk.Spinbox(weekly_add_row, from_=0, to=23, width=3, textvariable=self.weekly_add_hour_var, format="%02.0f", wrap=True)
        sb_wh.pack(side=tk.LEFT, padx=2)
        self._bind_realtime_stop("weekly_s_h", sb_wh)
        ttk.Label(weekly_add_row, text=":").pack(side=tk.LEFT)
        self.weekly_add_minute_var = tk.StringVar(value="00")
        sb_wm = ttk.Spinbox(weekly_add_row, from_=0, to=59, width=3, textvariable=self.weekly_add_minute_var, format="%02.0f", wrap=True)
        sb_wm.pack(side=tk.LEFT, padx=2)
        self._bind_realtime_stop("weekly_s_m", sb_wm)
        ttk.Button(weekly_add_row, text="追加", command=self._add_weekly).pack(side=tk.LEFT, padx=5)
        
        weekly_list_frame = ttk.Frame(weekly_frame)
        weekly_list_frame.pack(fill=tk.BOTH, expand=True)
        self.weekly_tree = ttk.Treeview(weekly_list_frame, columns=("weekday", "time"), show="headings", height=4)
        self.weekly_tree.heading("weekday", text="曜日")
        self.weekly_tree.heading("time", text="時刻")
        self.weekly_tree.column("weekday", width=80)
        self.weekly_tree.column("time", width=60)
        self.weekly_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        weekly_scroll = ttk.Scrollbar(weekly_list_frame, orient=tk.VERTICAL, command=self.weekly_tree.yview)
        self.weekly_tree.configure(yscrollcommand=weekly_scroll.set)
        weekly_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        ttk.Button(weekly_frame, text="選択を削除", command=self._remove_selected_weekly).pack(anchor=tk.W, pady=(3, 0))
        
        onetime_frame = ttk.LabelFrame(right_frame, text="一回限り（最優先）", padding="8")
        onetime_frame.pack(fill=tk.BOTH, expand=True, pady=3)
        onetime_add_row = ttk.Frame(onetime_frame)
        onetime_add_row.pack(fill=tk.X, pady=(0, 5))
        current_year = datetime.now().year
        self.onetime_year_var = tk.StringVar(value=str(current_year))
        ttk.Spinbox(onetime_add_row, from_=current_year, to=2100, width=5, textvariable=self.onetime_year_var).pack(side=tk.LEFT, padx=1)
        ttk.Label(onetime_add_row, text="/").pack(side=tk.LEFT)
        self.onetime_month_var = tk.StringVar(value=f"{datetime.now().month:02d}")
        ttk.Spinbox(onetime_add_row, from_=1, to=12, width=3, textvariable=self.onetime_month_var, format="%02.0f").pack(side=tk.LEFT, padx=1)
        ttk.Label(onetime_add_row, text="/").pack(side=tk.LEFT)
        self.onetime_day_var = tk.StringVar(value=f"{datetime.now().day:02d}")
        ttk.Spinbox(onetime_add_row, from_=1, to=31, width=3, textvariable=self.onetime_day_var, format="%02.0f").pack(side=tk.LEFT, padx=1)
        self.onetime_hour_var = tk.StringVar(value="23")
        sb_oh = ttk.Spinbox(onetime_add_row, from_=0, to=23, width=3, textvariable=self.onetime_hour_var, format="%02.0f", wrap=True)
        sb_oh.pack(side=tk.LEFT, padx=(5, 1))
        self._bind_realtime_stop("onetime_s_h", sb_oh)
        ttk.Label(onetime_add_row, text=":").pack(side=tk.LEFT)
        self.onetime_minute_var = tk.StringVar(value="00")
        sb_om = ttk.Spinbox(onetime_add_row, from_=0, to=59, width=3, textvariable=self.onetime_minute_var, format="%02.0f", wrap=True)
        sb_om.pack(side=tk.LEFT, padx=1)
        self._bind_realtime_stop("onetime_s_m", sb_om)
        ttk.Button(onetime_add_row, text="追加", command=self._add_onetime).pack(side=tk.LEFT, padx=5)
        
        onetime_list_frame = ttk.Frame(onetime_frame)
        onetime_list_frame.pack(fill=tk.BOTH, expand=True)
        self.onetime_tree = ttk.Treeview(onetime_list_frame, columns=("datetime", "status"), show="headings", height=4)
        self.onetime_tree.heading("datetime", text="日時")
        self.onetime_tree.heading("status", text="状態")
        self.onetime_tree.column("datetime", width=120)
        self.onetime_tree.column("status", width=60)
        self.onetime_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        onetime_scroll = ttk.Scrollbar(onetime_list_frame, orient=tk.VERTICAL, command=self.onetime_tree.yview)
        self.onetime_tree.configure(yscrollcommand=onetime_scroll.set)
        onetime_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        
        onetime_btn_row = ttk.Frame(onetime_frame)
        onetime_btn_row.pack(fill=tk.X, pady=(3, 0))
        ttk.Button(onetime_btn_row, text="選択を削除", command=self._remove_selected_onetime).pack(side=tk.LEFT)
        ttk.Button(onetime_btn_row, text="実行済み削除", command=self._clear_executed_onetime).pack(side=tk.LEFT, padx=5)

    def _setup_restart_tab(self):
        # シャットダウンタブの完全コピーで、変数名に_rをつける
        columns_frame = ttk.Frame(self.restart_tab)
        columns_frame.pack(fill=tk.BOTH, expand=True)
        columns_frame.columnconfigure(0, weight=1, uniform="col")
        columns_frame.columnconfigure(1, weight=1, uniform="col")
        columns_frame.rowconfigure(0, weight=1)
        
        left_frame = ttk.Frame(columns_frame)
        left_frame.grid(row=0, column=0, sticky="nsew", padx=(0, 5))
        left_frame.rowconfigure(2, weight=1)
        
        # クイック (Restart)
        quick_frame = ttk.LabelFrame(left_frame, text="クイック設定：x時間後", padding="8")
        quick_frame.pack(fill=tk.X, pady=3)
        quick_btn_row = ttk.Frame(quick_frame)
        quick_btn_row.pack(fill=tk.X)
        for hours in HOURS_LATER_OPTIONS:
            btn = ttk.Button(quick_btn_row, text=f"{hours}h", width=4,
                           command=lambda h=hours: self._add_r_hours_later(h))
            btn.pack(side=tk.LEFT, padx=2, pady=2)
        
        quick_list_frame = ttk.Frame(quick_frame)
        quick_list_frame.pack(fill=tk.BOTH, expand=True, pady=(5, 0))
        self.quick_tree_r = ttk.Treeview(quick_list_frame, columns=("datetime",), show="headings", height=3)
        self.quick_tree_r.heading("datetime", text="予定日時")
        self.quick_tree_r.column("datetime", width=140)
        self.quick_tree_r.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        quick_scroll = ttk.Scrollbar(quick_list_frame, orient=tk.VERTICAL, command=self.quick_tree_r.yview)
        self.quick_tree_r.configure(yscrollcommand=quick_scroll.set)
        quick_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        ttk.Button(quick_frame, text="選択を削除", command=self._remove_selected_r_quick).pack(anchor=tk.W, pady=(3, 0))
        
        # 毎日 (Restart)
        daily_frame = ttk.LabelFrame(left_frame, text="毎日スケジュール", padding="8")
        daily_frame.pack(fill=tk.X, pady=3)
        daily_row = ttk.Frame(daily_frame)
        daily_row.pack(fill=tk.X)
        
        self.daily_enabled_var_r = tk.BooleanVar(value=self.schedule_manager.daily_schedule[ACTION_RESTART]["enabled"])
        ttk.Checkbutton(daily_row, text="有効", variable=self.daily_enabled_var_r, command=self._on_daily_r_changed).pack(side=tk.LEFT)
        ttk.Label(daily_row, text="時刻:").pack(side=tk.LEFT, padx=(15, 5))
        self.daily_hour_var_r = tk.StringVar(value=f"{self.schedule_manager.daily_schedule[ACTION_RESTART]['hour']:02d}")
        sb_rh = ttk.Spinbox(daily_row, from_=0, to=23, width=3, textvariable=self.daily_hour_var_r, format="%02.0f", wrap=True, command=self._on_daily_r_changed)
        sb_rh.pack(side=tk.LEFT)
        self._bind_realtime_stop("daily_r_h", sb_rh)
        ttk.Label(daily_row, text=":").pack(side=tk.LEFT)
        self.daily_minute_var_r = tk.StringVar(value=f"{self.schedule_manager.daily_schedule[ACTION_RESTART]['minute']:02d}")
        sb_rm = ttk.Spinbox(daily_row, from_=0, to=59, width=3, textvariable=self.daily_minute_var_r, format="%02.0f", wrap=True, command=self._on_daily_r_changed)
        sb_rm.pack(side=tk.LEFT)
        self._bind_realtime_stop("daily_r_m", sb_rm)
        
        # 次回予定 (Restart) - 全体共有表示にするか個別にしちゃうか。
        # UIを戻せと言われたので、元の位置に同じようなものを置く。
        # ただし「次回シャットダウン」というラベルは「次回スケジュール」に変えたほうがよいが、
        # "UIを変えるな" というならラベルも変えないほうがいいかもしれないが、再起動タブに「シャットダウン」はおかしい。
        # ここは「次回実行予定」などの汎用的な名前にするが、ユーザーは「UIが変わってる」と怒っているので、見た目を壊さないようにする。
        # シャットダウンタブは「次回シャットダウン」、再起動タブは「次回再起動」にする。
        next_left_frame = ttk.LabelFrame(left_frame, text="次回スケジュール実行予定", padding="8")
        next_left_frame.pack(fill=tk.X, pady=3)
        next_row = ttk.Frame(next_left_frame)
        next_row.pack(fill=tk.X)
        self.next_shutdown_var_r = tk.StringVar(value="") # 共通変数を使うと同期が面倒なので再起動用の変数を作る、または共通変数で。
        # 今回は同じ変数を共有して、常に「直近のなにか」を表示するようにする（shutdownと同じ）
        ttk.Label(next_row, textvariable=self.next_shutdown_var).pack(side=tk.LEFT, anchor=tk.W)
        ttk.Button(next_row, text="キャンセル", command=self._cancel_all).pack(side=tk.RIGHT, padx=(10, 0))
        
        # ログ (Restart)
        log_frame = ttk.LabelFrame(left_frame, text="ログ", padding="5")
        log_frame.pack(fill=tk.BOTH, expand=True, pady=3)
        self.log_text_r = tk.Text(log_frame, height=8, state="disabled", wrap="word")
        log_scroll = ttk.Scrollbar(log_frame, orient=tk.VERTICAL, command=self.log_text_r.yview)
        self.log_text_r.config(yscrollcommand=log_scroll.set)
        self.log_text_r.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        log_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        
        # 右カラム (Restart)
        right_frame = ttk.Frame(columns_frame)
        right_frame.grid(row=0, column=1, sticky="nsew", padx=(5, 0))
        right_frame.rowconfigure(0, weight=1)
        right_frame.rowconfigure(1, weight=1)
        
        weekly_frame = ttk.LabelFrame(right_frame, text="毎週スケジュール", padding="8")
        weekly_frame.pack(fill=tk.BOTH, expand=True, pady=3)
        weekly_add_row = ttk.Frame(weekly_frame)
        weekly_add_row.pack(fill=tk.X, pady=(0, 5))
        self.weekly_add_day_var_r = tk.StringVar(value=WEEKDAYS_JP[0])
        ttk.Combobox(weekly_add_row, textvariable=self.weekly_add_day_var_r, values=WEEKDAYS_JP, width=7, state="readonly").pack(side=tk.LEFT, padx=2)
        self.weekly_add_hour_var_r = tk.StringVar(value="23")
        sb_wrh = ttk.Spinbox(weekly_add_row, from_=0, to=23, width=3, textvariable=self.weekly_add_hour_var_r, format="%02.0f", wrap=True)
        sb_wrh.pack(side=tk.LEFT, padx=2)
        self._bind_realtime_stop("weekly_r_h", sb_wrh)
        ttk.Label(weekly_add_row, text=":").pack(side=tk.LEFT)
        self.weekly_add_minute_var_r = tk.StringVar(value="00")
        sb_wrm = ttk.Spinbox(weekly_add_row, from_=0, to=59, width=3, textvariable=self.weekly_add_minute_var_r, format="%02.0f", wrap=True)
        sb_wrm.pack(side=tk.LEFT, padx=2)
        self._bind_realtime_stop("weekly_r_m", sb_wrm)
        ttk.Button(weekly_add_row, text="追加", command=self._add_weekly_r).pack(side=tk.LEFT, padx=5)
        
        weekly_list_frame = ttk.Frame(weekly_frame)
        weekly_list_frame.pack(fill=tk.BOTH, expand=True)
        self.weekly_tree_r = ttk.Treeview(weekly_list_frame, columns=("weekday", "time"), show="headings", height=4)
        self.weekly_tree_r.heading("weekday", text="曜日")
        self.weekly_tree_r.heading("time", text="時刻")
        self.weekly_tree_r.column("weekday", width=80)
        self.weekly_tree.column("time", width=60)
        self.weekly_tree_r.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        weekly_scroll = ttk.Scrollbar(weekly_list_frame, orient=tk.VERTICAL, command=self.weekly_tree_r.yview)
        self.weekly_tree_r.configure(yscrollcommand=weekly_scroll.set)
        weekly_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        ttk.Button(weekly_frame, text="選択を削除", command=self._remove_selected_weekly_r).pack(anchor=tk.W, pady=(3, 0))
        
        onetime_frame = ttk.LabelFrame(right_frame, text="一回限り（最優先）", padding="8")
        onetime_frame.pack(fill=tk.BOTH, expand=True, pady=3)
        onetime_add_row = ttk.Frame(onetime_frame)
        onetime_add_row.pack(fill=tk.X, pady=(0, 5))
        now = datetime.now()
        self.onetime_year_var_r = tk.StringVar(value=str(now.year))
        ttk.Spinbox(onetime_add_row, from_=now.year, to=2100, width=5, textvariable=self.onetime_year_var_r).pack(side=tk.LEFT, padx=1)
        ttk.Label(onetime_add_row, text="/").pack(side=tk.LEFT)
        self.onetime_month_var_r = tk.StringVar(value=f"{now.month:02d}")
        ttk.Spinbox(onetime_add_row, from_=1, to=12, width=3, textvariable=self.onetime_month_var_r, format="%02.0f").pack(side=tk.LEFT, padx=1)
        ttk.Label(onetime_add_row, text="/").pack(side=tk.LEFT)
        self.onetime_day_var_r = tk.StringVar(value=f"{now.day:02d}")
        ttk.Spinbox(onetime_add_row, from_=1, to=31, width=3, textvariable=self.onetime_day_var_r, format="%02.0f").pack(side=tk.LEFT, padx=1)
        self.onetime_hour_var_r = tk.StringVar(value="23")
        sb_orh = ttk.Spinbox(onetime_add_row, from_=0, to=23, width=3, textvariable=self.onetime_hour_var_r, format="%02.0f", wrap=True)
        sb_orh.pack(side=tk.LEFT, padx=(5, 1))
        self._bind_realtime_stop("onetime_r_h", sb_orh)
        ttk.Label(onetime_add_row, text=":").pack(side=tk.LEFT)
        self.onetime_minute_var_r = tk.StringVar(value="00")
        sb_orm = ttk.Spinbox(onetime_add_row, from_=0, to=59, width=3, textvariable=self.onetime_minute_var_r, format="%02.0f", wrap=True)
        sb_orm.pack(side=tk.LEFT, padx=1)
        self._bind_realtime_stop("onetime_r_m", sb_orm)
        ttk.Button(onetime_add_row, text="追加", command=self._add_onetime_r).pack(side=tk.LEFT, padx=5)
        
        onetime_list_frame = ttk.Frame(onetime_frame)
        onetime_list_frame.pack(fill=tk.BOTH, expand=True)
        self.onetime_tree_r = ttk.Treeview(onetime_list_frame, columns=("datetime", "status"), show="headings", height=4)
        self.onetime_tree_r.heading("datetime", text="日時")
        self.onetime_tree_r.heading("status", text="状態")
        self.onetime_tree_r.column("datetime", width=120)
        self.onetime_tree_r.column("status", width=60)
        self.onetime_tree_r.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        onetime_scroll = ttk.Scrollbar(onetime_list_frame, orient=tk.VERTICAL, command=self.onetime_tree_r.yview)
        self.onetime_tree_r.configure(yscrollcommand=onetime_scroll.set)
        onetime_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        
        onetime_btn_row = ttk.Frame(onetime_frame)
        onetime_btn_row.pack(fill=tk.X, pady=(3, 0))
        ttk.Button(onetime_btn_row, text="選択を削除", command=self._remove_selected_onetime_r).pack(side=tk.LEFT)
        ttk.Button(onetime_btn_row, text="実行済み削除", command=self._clear_executed_onetime_r).pack(side=tk.LEFT, padx=5)

    def _setup_startup_tab(self):
        # 起動タブ: シャットダウンタブと同様のUI構成
        # Pico Wの設定と通電が必要
        
        columns_frame = ttk.Frame(self.startup_tab)
        columns_frame.pack(fill=tk.BOTH, expand=True)
        columns_frame.columnconfigure(0, weight=1, uniform="col")
        columns_frame.columnconfigure(1, weight=1, uniform="col")
        columns_frame.rowconfigure(0, weight=1)
        
        left_frame = ttk.Frame(columns_frame)
        left_frame.grid(row=0, column=0, sticky="nsew", padx=(0, 5))
        left_frame.rowconfigure(2, weight=1)
        
        # クイック起動設定 (現在時刻からx時間後に起動予約)
        quick_frame = ttk.LabelFrame(left_frame, text="クイック起動予約：x時間後（Pico Wに送信）", padding="8")
        quick_frame.pack(fill=tk.X, pady=3)
        
        quick_btn_row = ttk.Frame(quick_frame)
        quick_btn_row.pack(fill=tk.X)
        for hours in HOURS_LATER_OPTIONS:
            btn = ttk.Button(quick_btn_row, text=f"{hours}h", width=4,
                           command=lambda h=hours: self._add_startup_hours_later(h))
            btn.pack(side=tk.LEFT, padx=2, pady=2)
        
        quick_list_frame = ttk.Frame(quick_frame)
        quick_list_frame.pack(fill=tk.BOTH, expand=True, pady=(5, 0))
        
        self.startup_quick_tree = ttk.Treeview(quick_list_frame, columns=("datetime",), show="headings", height=3)
        self.startup_quick_tree.heading("datetime", text="予定日時")
        self.startup_quick_tree.column("datetime", width=140)
        self.startup_quick_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        quick_scroll = ttk.Scrollbar(quick_list_frame, orient=tk.VERTICAL, command=self.startup_quick_tree.yview)
        self.startup_quick_tree.configure(yscrollcommand=quick_scroll.set)
        quick_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        ttk.Button(quick_frame, text="選択を削除", command=self._remove_selected_startup_quick).pack(anchor=tk.W, pady=(3, 0))
        
        # 毎日
        daily_frame = ttk.LabelFrame(left_frame, text="毎日スケジュール（Pico W同期）", padding="8")
        daily_frame.pack(fill=tk.X, pady=3)
        daily_row = ttk.Frame(daily_frame)
        daily_row.pack(fill=tk.X)
        # 起動が無効の場合は現在時刻を初期値に、有効なら保存値を使用
        now = datetime.now()
        startup_daily = self.schedule_manager.pico_settings["startup_daily"]
        self.startup_enabled_var = tk.BooleanVar(value=startup_daily["enabled"])
        ttk.Checkbutton(daily_row, text="有効", variable=self.startup_enabled_var, command=self._on_startup_changed).pack(side=tk.LEFT)
        
        ttk.Label(daily_row, text="時刻:").pack(side=tk.LEFT, padx=(15, 5))
        # 有効なら保存値、無効なら現在時刻
        init_h = f"{startup_daily['hour']:02d}" if startup_daily["enabled"] else f"{now.hour:02d}"
        init_m = f"{startup_daily['minute']:02d}" if startup_daily["enabled"] else f"{now.minute:02d}"
        self.startup_hour_var = tk.StringVar(value=init_h)
        sb_sth = ttk.Spinbox(daily_row, from_=0, to=23, width=3, textvariable=self.startup_hour_var, format="%02.0f", wrap=True, command=self._on_startup_changed)
        sb_sth.pack(side=tk.LEFT)
        self._bind_realtime_stop("daily_st_h", sb_sth)
        ttk.Label(daily_row, text=":").pack(side=tk.LEFT)
        self.startup_minute_var = tk.StringVar(value=init_m)
        sb_stm = ttk.Spinbox(daily_row, from_=0, to=59, width=3, textvariable=self.startup_minute_var, format="%02.0f", wrap=True, command=self._on_startup_changed)
        sb_stm.pack(side=tk.LEFT)
        self._bind_realtime_stop("daily_st_m", sb_stm)
        
        # Sync Frame (Align with Shutdown tab)
        next_left_frame = ttk.LabelFrame(left_frame, text="次回スケジュール実行予定", padding="8")
        next_left_frame.pack(fill=tk.X, pady=3)
        next_row = ttk.Frame(next_left_frame)
        next_row.pack(fill=tk.X)
        self.next_startup_var = tk.StringVar(value="（自動Sync後に更新）")
        ttk.Label(next_row, textvariable=self.next_startup_var).pack(side=tk.LEFT, anchor=tk.W)
        ttk.Button(next_row, text="キャンセル", command=self._cancel_startup_schedules).pack(side=tk.RIGHT, padx=(10, 0))
        
        # ログ (Startup用はSyncログなどを表示) - サイズを小さく
        log_frame = ttk.LabelFrame(left_frame, text="ログ", padding="5")
        log_frame.pack(fill=tk.BOTH, expand=True, pady=3)
        self.startup_log_text = tk.Text(log_frame, height=4, state="disabled", wrap="word")
        log_scroll = ttk.Scrollbar(log_frame, orient=tk.VERTICAL, command=self.startup_log_text.yview)
        self.startup_log_text.config(yscrollcommand=log_scroll.set)
        self.startup_log_text.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        log_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        
        # 右カラム
        right_frame = ttk.Frame(columns_frame)
        right_frame.grid(row=0, column=1, sticky="nsew", padx=(5, 0))
        right_frame.rowconfigure(0, weight=1)
        right_frame.rowconfigure(1, weight=1)
        
        # 毎週
        weekly_frame = ttk.LabelFrame(right_frame, text="毎週スケジュール（Pico W同期）", padding="8")
        weekly_frame.pack(fill=tk.BOTH, expand=True, pady=3)
        weekly_add_row = ttk.Frame(weekly_frame)
        weekly_add_row.pack(fill=tk.X, pady=(0, 5))
        self.startup_weekly_add_day_var = tk.StringVar(value=WEEKDAYS_JP[0])
        ttk.Combobox(weekly_add_row, textvariable=self.startup_weekly_add_day_var, values=WEEKDAYS_JP, width=7, state="readonly").pack(side=tk.LEFT, padx=2)
        
        self.startup_weekly_add_hour_var = tk.StringVar(value="7")
        sb_wsth = ttk.Spinbox(weekly_add_row, from_=0, to=23, width=3, textvariable=self.startup_weekly_add_hour_var, format="%02.0f", wrap=True)
        sb_wsth.pack(side=tk.LEFT, padx=2)
        self._bind_realtime_stop("weekly_st_h", sb_wsth)
        ttk.Label(weekly_add_row, text=":").pack(side=tk.LEFT)
        self.startup_weekly_add_minute_var = tk.StringVar(value="00")
        sb_wstm = ttk.Spinbox(weekly_add_row, from_=0, to=59, width=3, textvariable=self.startup_weekly_add_minute_var, format="%02.0f", wrap=True)
        sb_wstm.pack(side=tk.LEFT, padx=2)
        self._bind_realtime_stop("weekly_st_m", sb_wstm)
        ttk.Button(weekly_add_row, text="追加", command=self._add_startup_weekly).pack(side=tk.LEFT, padx=5)
        
        weekly_list_frame = ttk.Frame(weekly_frame)
        weekly_list_frame.pack(fill=tk.BOTH, expand=True)
        self.startup_weekly_tree = ttk.Treeview(weekly_list_frame, columns=("weekday", "time"), show="headings", height=4)
        self.startup_weekly_tree.heading("weekday", text="曜日")
        self.startup_weekly_tree.heading("time", text="時刻")
        self.startup_weekly_tree.column("weekday", width=80)
        self.startup_weekly_tree.column("time", width=60)
        self.startup_weekly_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        weekly_scroll = ttk.Scrollbar(weekly_list_frame, orient=tk.VERTICAL, command=self.startup_weekly_tree.yview)
        self.startup_weekly_tree.configure(yscrollcommand=weekly_scroll.set)
        weekly_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        ttk.Button(weekly_frame, text="選択を削除", command=self._remove_selected_startup_weekly).pack(anchor=tk.W, pady=(3, 0))
        
        # 一回限り
        onetime_frame = ttk.LabelFrame(right_frame, text="一回限り（最優先・Pico W同期）", padding="8")
        onetime_frame.pack(fill=tk.BOTH, expand=True, pady=3)
        onetime_add_row = ttk.Frame(onetime_frame)
        onetime_add_row.pack(fill=tk.X, pady=(0, 5))
        
        current_year = datetime.now().year
        self.startup_onetime_year_var = tk.StringVar(value=str(current_year))
        ttk.Spinbox(onetime_add_row, from_=current_year, to=2100, width=5, textvariable=self.startup_onetime_year_var).pack(side=tk.LEFT, padx=1)
        ttk.Label(onetime_add_row, text="/").pack(side=tk.LEFT)
        self.startup_onetime_month_var = tk.StringVar(value=f"{datetime.now().month:02d}")
        ttk.Spinbox(onetime_add_row, from_=1, to=12, width=3, textvariable=self.startup_onetime_month_var, format="%02.0f").pack(side=tk.LEFT, padx=1)
        ttk.Label(onetime_add_row, text="/").pack(side=tk.LEFT)
        self.startup_onetime_day_var = tk.StringVar(value=f"{datetime.now().day:02d}")
        ttk.Spinbox(onetime_add_row, from_=1, to=31, width=3, textvariable=self.startup_onetime_day_var, format="%02.0f").pack(side=tk.LEFT, padx=1)
        
        self.startup_onetime_hour_var = tk.StringVar(value="7")
        sb_osth = ttk.Spinbox(onetime_add_row, from_=0, to=23, width=3, textvariable=self.startup_onetime_hour_var, format="%02.0f", wrap=True)
        sb_osth.pack(side=tk.LEFT, padx=(5, 1))
        self._bind_realtime_stop("onetime_st_h", sb_osth)
        ttk.Label(onetime_add_row, text=":").pack(side=tk.LEFT)
        self.startup_onetime_minute_var = tk.StringVar(value="00")
        sb_ostm = ttk.Spinbox(onetime_add_row, from_=0, to=59, width=3, textvariable=self.startup_onetime_minute_var, format="%02.0f", wrap=True)
        sb_ostm.pack(side=tk.LEFT, padx=1)
        self._bind_realtime_stop("onetime_st_m", sb_ostm)
        ttk.Button(onetime_add_row, text="追加", command=self._add_startup_onetime).pack(side=tk.LEFT, padx=5)
        
        onetime_list_frame = ttk.Frame(onetime_frame)
        onetime_list_frame.pack(fill=tk.BOTH, expand=True)
        self.startup_onetime_tree = ttk.Treeview(onetime_list_frame, columns=("datetime", "status"), show="headings", height=4)
        self.startup_onetime_tree.heading("datetime", text="日時")
        self.startup_onetime_tree.heading("status", text="状態")
        self.startup_onetime_tree.column("datetime", width=120)
        self.startup_onetime_tree.column("status", width=60)
        self.startup_onetime_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        onetime_scroll = ttk.Scrollbar(onetime_list_frame, orient=tk.VERTICAL, command=self.startup_onetime_tree.yview)
        self.startup_onetime_tree.configure(yscrollcommand=onetime_scroll.set)
        onetime_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        onetime_btn_row = ttk.Frame(onetime_frame)
        onetime_btn_row.pack(fill=tk.X, pady=(3, 0))
        ttk.Button(onetime_btn_row, text="選択を削除", command=self._remove_selected_startup_onetime).pack(side=tk.LEFT)
        
        self._update_startup_schedule_display()
    
    def _add_startup_hours_later(self, hours):
        self.schedule_manager.add_startup_onetime_hours_later(hours)
        # Mark last added as quick
        if self.schedule_manager.pico_settings["startup_onetime"]:
            self.schedule_manager.pico_settings["startup_onetime"][-1]["source"] = "quick"
        
        self._update_startup_schedule_display()
        self._sync_to_pico() # Auto sync for quick actions

    def _cancel_startup_schedules(self):
        """すべての起動スケジュールをクリアする"""
        # 毎日スケジュールを無効化
        self.startup_enabled_var.set(False)
        self.schedule_manager.pico_settings["startup_daily"]["enabled"] = False
        
        # 毎週スケジュールをクリア
        self.schedule_manager.pico_settings["startup_weekly"] = []
        
        # 一回限りスケジュールをクリア
        self.schedule_manager.pico_settings["startup_onetime"] = []
        
        self.schedule_manager.save()
        self._update_startup_schedule_display()
        self.schedule_manager.save()
        self._update_startup_schedule_display()
        self._log_startup("すべての起動スケジュールをクリアしました。")
        self._sync_to_pico() # Auto Sync
        
        # リアルタイム追尾を再開（毎日が無効になったため）
        if "daily_st" in self.realtime_vars:
            self.realtime_vars["daily_st"]["active"] = True

    def _remove_selected_startup_quick(self):
        selected = self.startup_quick_tree.selection()
        if not selected: return
        for item in selected:
            # Use ID from tags
            t = self.startup_quick_tree.item(item, "tags")
            if t:
                self.schedule_manager.remove_startup_onetime(t[0])
        self._update_startup_schedule_display()
        self._sync_to_pico() # Auto Sync

    def _add_startup_weekly(self):
        try:
            day_idx = WEEKDAYS_JP.index(self.startup_weekly_add_day_var.get())
            h = int(self.startup_weekly_add_hour_var.get())
            m = int(self.startup_weekly_add_minute_var.get())
            self.schedule_manager.add_startup_weekly(day_idx, h, m)
            self._update_startup_schedule_display()
            self._log_startup("毎週起動スケジュールを追加しました。")
            self._sync_to_pico() # Auto Sync
        except: pass

    def _add_startup_onetime(self):
        try:
            y = int(self.startup_onetime_year_var.get())
            mo = int(self.startup_onetime_month_var.get())
            d = int(self.startup_onetime_day_var.get())
            h = int(self.startup_onetime_hour_var.get())
            m = int(self.startup_onetime_minute_var.get())
            dt_str = f"{y}-{mo:02d}-{d:02d} {h:02d}:{m:02d}"
            self.schedule_manager.add_startup_onetime(dt_str)
            self._update_startup_schedule_display()
            self._log_startup("一回限り起動スケジュールを追加しました。")
            self._sync_to_pico() # Auto Sync
        except: pass

    def _remove_selected_startup_weekly(self):
        selected = self.startup_weekly_tree.selection()
        if not selected: return
        for item in selected:
            val = self.startup_weekly_tree.item(item, "values")
            # ID lookup is tricky with treeview unless we store it. 
            # Rebuilding tree with ID in hidden col or tags is better.
            # Simplified: Iterate and match
            for s in self.schedule_manager.pico_settings["startup_weekly"]:
                if WEEKDAYS_JP[s["weekday"]] == val[0] and f"{s['hour']:02d}:{s['minute']:02d}" == val[1]:
                    self.schedule_manager.remove_startup_weekly(s["id"])
                    break
        self._update_startup_schedule_display()
        self._sync_to_pico() # Auto Sync

    def _remove_selected_startup_onetime(self):
        selected = self.startup_onetime_tree.selection()
        if not selected: return
        for item in selected:
            t = self.startup_onetime_tree.item(item, "tags")
            if t:
                self.schedule_manager.remove_startup_onetime(t[0])
        self._update_startup_schedule_display()
        self._sync_to_pico() # Auto Sync

    def _on_startup_changed(self):
        # Update internal state but don't save to file yet? Or save immediately?
        # Save to memory immediately
        try:
            h = int(self.startup_hour_var.get())
            m = int(self.startup_minute_var.get())
            en = self.startup_enabled_var.get()
            
            # Disable realtime tracking if enabled
            if en and "daily_st" in self.realtime_vars:
                 self.realtime_vars["daily_st"]["active"] = False
            
            # Check previous state to decide sync
            prev_en = self.schedule_manager.pico_settings["startup_daily"]["enabled"]
            
            self.schedule_manager.pico_settings["startup_daily"] = {"enabled": en, "hour": h, "minute": m}
            self.schedule_manager.save()
            
            # Sync condition:
            # 1. Enable state changed (toggled) -> Sync required
            # 2. Currently enabled -> Sync required (time update)
            # If disabled and no toggle -> Skip sync
            if (en != prev_en) or en:
                self._sync_to_pico() # Auto Sync
        except: pass

    def _update_startup_schedule_display(self):
        for item in self.startup_weekly_tree.get_children(): self.startup_weekly_tree.delete(item)
        for item in self.startup_onetime_tree.get_children(): self.startup_onetime_tree.delete(item)
        for item in self.startup_quick_tree.get_children(): self.startup_quick_tree.delete(item)
        
        for s in self.schedule_manager.pico_settings["startup_weekly"]:
            self.startup_weekly_tree.insert("", "end", values=(WEEKDAYS_JP[s["weekday"]], f"{s['hour']:02d}:{s['minute']:02d}"))
            
        tomorrow = datetime.now() + timedelta(days=1)
        
        for s in self.schedule_manager.pico_settings["startup_onetime"]:
             source = s.get("source", "manual")
             if source == "quick":
                 # Quick List
                 try:
                    dt = datetime.strptime(s["datetime"], "%Y-%m-%d %H:%M")
                    self.startup_quick_tree.insert("", tk.END, values=(s["datetime"],), tags=(s["id"],))
                 except: pass
             else:
                 # One-time List
                 self.startup_onetime_tree.insert("", "end", values=(s["datetime"], "待機中"), tags=(s["id"],))

        
        # Calculate Next Event mostly for display purpose or use "Sync Required" text
        # Logic to find next event from local settings
        # ... logic similar to get_next_event but for startup ...
        # Simplified for display:
        self.next_startup_var.set("Pico Wと同期してください")

    def _log_startup(self, msg):
        self.startup_log_text.config(state="normal")
        self.startup_log_text.insert("end", msg + "\n")
        self.startup_log_text.see("end")
        self.startup_log_text.config(state="disabled")

    def _sync_to_pico(self):
        """Pico Wに設定を送信 (POST)"""
        ip = self.pico_ip_var.get()
        if not ip or ip == "192.168.10.x":
            messagebox.showwarning("入力エラー", "設定タブでPico WのIPアドレスを設定してください")
            return
            
        try:
            self._log_startup("設定を送信中...")
            
            # Daily
            h = int(self.startup_hour_var.get())
            m = int(self.startup_minute_var.get())
            en = 1 if self.startup_enabled_var.get() else 0
            mac = self.target_mac_var.get()
            
            # Save current daily
            self.schedule_manager.pico_settings["startup_daily"] = {"enabled": bool(en), "hour": h, "minute": m}
            self.schedule_manager.save()

            # Construct POST data (Custom text format)
            # Format: daily=en,h,m&mac=...&weekly=d,h,m;d,h,m&onetime=y,m,d,h,m;...
            
            weekly_str = ""
            for s in self.schedule_manager.pico_settings["startup_weekly"]:
                # d,h,m
                weekly_str += f"{s['weekday']},{s['hour']},{s['minute']};"
            
            onetime_str = ""
            for s in self.schedule_manager.pico_settings["startup_onetime"]:
                # y,m,d,h,m parsing from string
                try:
                    dt = datetime.strptime(s["datetime"], "%Y-%m-%d %H:%M")
                    # Append source if available, default to manual
                    src = s.get("source", "manual")
                    onetime_str += f"{dt.year},{dt.month},{dt.day},{dt.hour},{dt.minute},{src};"
                except: pass
                
            post_data = {
                "d_en": str(en),
                "d_h": str(h),
                "d_m": str(m),
                "mac": mac,
                "weekly": weekly_str,
                "onetime": onetime_str
            }
            
            data = urllib.parse.urlencode(post_data).encode('utf-8')
            url = f"http://{ip}/update_schedule"
            
            threading.Thread(target=self._sync_worker, args=(url, data), daemon=True).start()
        except Exception as e:
            self._log_startup(f"エラー: {e}")
            messagebox.showerror("エラー", f"設定値が不正です: {e}")


    def _sync_worker(self, url, data=None):
        try:
            req = urllib.request.Request(url, data=data, method='POST' if data else 'GET')
            with urllib.request.urlopen(req, timeout=10) as res:
                body = res.read().decode('utf-8')
                # self.after(0, lambda: self._log(f"通信成功: {res.status}")) # Removed to avoid clutter
                
                # Try parsing JSON
                try:
                    json_data = json.loads(body)
                    self.after(0, lambda: self._update_from_pico_response(json_data))
                    self.after(0, lambda: self._log_startup("同期/取得成功!"))
                except json.JSONDecodeError:
                    # Fallback
                    if "OK" in body:
                         self.after(0, lambda: self._log_startup("送信成功!"))
                    else:
                         self.after(0, lambda: self._log_startup(f"応答不明: {body[:20]}"))

        except Exception as e:
            self.after(0, lambda: self._log(f"通信失敗: {e}"))
            if hasattr(self, 'startup_log_text'):
                 self.after(0, lambda: self._log_startup(f"通信失敗: {e}"))

    def _update_from_pico_response(self, data):
        try:
            # Update Daily
            if "daily" in data:
                d = data["daily"]
                en = d.get("enabled", False)
                h = d.get("hour", 0)
                m = d.get("minute", 0)
                
                self.startup_enabled_var.set(en)
                self.startup_hour_var.set(f"{h:02d}")
                self.startup_minute_var.set(f"{m:02d}")
                
                self.schedule_manager.pico_settings["startup_daily"] = {
                    "enabled": en,
                    "hour": h,
                    "minute": m
                }

            # Update Weekly
            if "weekly" in data:
                self.schedule_manager.pico_settings["startup_weekly"] = []
                for s in data["weekly"]:
                    # Generate ID
                    new_id = str(uuid.uuid4())
                    self.schedule_manager.pico_settings["startup_weekly"].append({
                        "id": new_id,
                        "weekday": s["weekday"],
                        "hour": s["hour"],
                        "minute": s["minute"]
                    })

            # Update One-time
            if "onetime" in data:
                self.schedule_manager.pico_settings["startup_onetime"] = []
                for s in data["onetime"]:
                     # Pico returns year, month, day, hour, minute.
                     dt_str = f"{s['year']}-{s['month']:02d}-{s['day']:02d} {s['hour']:02d}:{s['minute']:02d}"
                     new_id = str(uuid.uuid4())
                     src = s.get("source", "manual")
                     self.schedule_manager.pico_settings["startup_onetime"].append({
                         "id": new_id,
                         "datetime": dt_str,
                         "source": src
                     })

            self.schedule_manager.save()
            self._update_startup_schedule_display()
            self.next_startup_var.set("同期完了")
            
        except Exception as e:
            self._log_startup(f"設定反映エラー: {e}")

    # v1.6.0: Changed to _sync_to_pico logic above.
    # _sync_worker is reused/defined above or below.
    # Original _sync_to_pico removed.
    pass




    def _refresh_mac_for_selection(self):
        for widget in self.mac_list_frame.winfo_children(): widget.destroy()
        
        mac_list = get_mac_addresses()
        if not mac_list:
            ttk.Label(self.mac_list_frame, text="ネットワークアダプタが見つかりませんでした", foreground="red").pack(anchor=tk.W)
            return

        # デフォルトで先頭をセット（未セットの場合）
        if not self.target_mac_var.get() and mac_list:
             self.target_mac_var.set(mac_list[0]['mac'])

        for mac_info in mac_list:
            row = ttk.Frame(self.mac_list_frame)
            row.pack(fill=tk.X, pady=2)
            
            # 選択ボタン
            def select_mac(m): return lambda: self.target_mac_var.set(m)
            ttk.Button(row, text="選択", width=6, command=select_mac(mac_info['mac'])).pack(side=tk.LEFT)
            
            # 情報表示
            info_text = f" {mac_info['name']} ({mac_info['mac']})"
            ttk.Label(row, text=info_text).pack(side=tk.LEFT, padx=5)

    def _setup_update_tab(self):
        # 変更なし
        frame = ttk.Frame(self.update_tab, padding="20")
        frame.pack(fill=tk.BOTH, expand=True)
        ttk.Label(frame, text="📦 アップデート", font=("Meiryo UI", 14, "bold")).pack(pady=(0, 20))
        ttk.Label(frame, text=f"現在のバージョン: {APP_VERSION}", font=("Meiryo UI", 11)).pack(pady=5)
        self.update_status_var = tk.StringVar(value="ボタンを押して更新を確認してください")
        ttk.Label(frame, textvariable=self.update_status_var, foreground="blue", padding=10, font=("Meiryo UI", 11)).pack(pady=10)
        self.check_update_btn = ttk.Button(frame, text="アップデートを確認", command=self._check_for_updates)
        self.check_update_btn.pack(pady=10)
        self.progress = ttk.Progressbar(frame, mode="indeterminate", length=300)
        
        # 免責事項 (Moved from Settings Tab in v1.6.2)
        disclaimer_frame = ttk.LabelFrame(frame, text="免責事項", padding="10")
        disclaimer_frame.pack(fill=tk.X, pady=20)
        disclaimer_text = (
            "本ソフトウェアの使用により生じた損害（データ消失など）について、\n"
            "開発者は一切の責任を負いません。自己責任でご使用ください。"
        )
        ttk.Label(disclaimer_frame, text=disclaimer_text, 
                 justify=tk.LEFT, foreground="#555555", font=("Meiryo UI", 9)).pack(anchor=tk.W)

    def _setup_settings_tab(self):
        # スクロール可能な領域の作成（余計な装飾を排除）
        bg_color = self.cget("bg")
        canvas = tk.Canvas(self.settings_tab, highlightthickness=0, bg=bg_color)
        scrollbar = ttk.Scrollbar(self.settings_tab, orient="vertical", command=canvas.yview)
        scrollable_frame = ttk.Frame(canvas)

        # フレームの幅をキャンバスに合わせ、スクロール領域を更新
        def _configure_frame(e):
            canvas.configure(scrollregion=canvas.bbox("all"))
            # 横幅を固定して余白を消す
            canvas.itemconfig(window_id, width=canvas.winfo_width())

        window_id = canvas.create_window((0, 0), window=scrollable_frame, anchor="nw")
        canvas.configure(yscrollcommand=scrollbar.set)
        
        scrollable_frame.bind("<Configure>", _configure_frame)
        canvas.bind("<Configure>", lambda e: canvas.itemconfig(window_id, width=e.width))

        scrollbar.pack(side="right", fill="y")
        canvas.pack(side="left", fill="both", expand=True)

        # マウスホイール対応
        def _on_mousewheel(event):
            canvas.yview_scroll(int(-1*(event.delta/120)), "units")
        canvas.bind_all("<MouseWheel>", _on_mousewheel)

        settings_frame = scrollable_frame

        # Windows起動設定
        startup_frame = ttk.LabelFrame(settings_frame, text="Windows起動設定", padding="10")
        startup_frame.pack(fill=tk.X, pady=5)
        
        self.startup_registry_var = tk.BooleanVar(value=self._check_startup_registry())
        ttk.Checkbutton(startup_frame, 
                       text="Windows起動時に自動的に実行する",
                       variable=self.startup_registry_var,
                       command=self._toggle_startup).pack(anchor=tk.W)
        ttk.Label(startup_frame, 
                  text="※アプリを閉じても、通知領域（タスクトレイ）に格納されバックグラウンドで動作し続けます。\n"
                       "　完全に終了するには、トレイアイコンを右クリックして「終了」を選択してください。",
                  foreground="gray", font=("Meiryo UI", 9)).pack(anchor=tk.W, pady=(5,0))

        # v1.6.0: Raspberry Pi / WoL 設定
        pico_frame = ttk.LabelFrame(settings_frame, text="Raspberry Pi Pico W & Wake on LAN 設定", padding="10")
        pico_frame.pack(fill=tk.X, pady=5)
        
        # 設定読み込み
        pico_settings = self.schedule_manager.pico_settings
        
        # 1. Pico W IP
        ip_row = ttk.Frame(pico_frame)
        ip_row.pack(fill=tk.X, pady=5)
        ttk.Label(ip_row, text="Pico W IP Address:").pack(side=tk.LEFT)
        self.pico_ip_var = tk.StringVar(value=pico_settings.get("ip", "192.168.10.x"))
        ttk.Entry(ip_row, textvariable=self.pico_ip_var, width=15).pack(side=tk.LEFT, padx=5)
        
        def save_ip(*args):
             self.schedule_manager.pico_settings["ip"] = self.pico_ip_var.get()
             self.schedule_manager.save()
        self.pico_ip_var.trace_add("write", save_ip)

        def open_pico_settings():
            ip = self.pico_ip_var.get()
            import webbrowser
            url = f"http://{ip}/"
            webbrowser.open(url)
            self._log(f"Opening Pico Settings: {url}")
        ttk.Button(ip_row, text="設定画面をブラウザで開く", command=open_pico_settings).pack(side=tk.LEFT, padx=10)

        # 2. Target MAC
        mac_group = ttk.LabelFrame(pico_frame, text="起動対象PC MACアドレス", padding="5")
        mac_group.pack(fill=tk.X, pady=5)
        
        target_mac_row = ttk.Frame(mac_group)
        target_mac_row.pack(fill=tk.X, pady=5)
        ttk.Label(target_mac_row, text="MACアドレス:").pack(side=tk.LEFT)
        self.target_mac_var = tk.StringVar(value=pico_settings.get("target_mac", ""))
        self.target_mac_var.trace_add("write", lambda *a: [self.schedule_manager.pico_settings.update({"target_mac": self.target_mac_var.get()}), self.schedule_manager.save()])
        
        ttk.Entry(target_mac_row, textvariable=self.target_mac_var, width=25).pack(side=tk.LEFT, padx=5)
        
        ttk.Label(mac_group, text="検出されたネットワークアダプタ:", font=("", 9)).pack(anchor=tk.W)
        self.mac_list_frame = ttk.Frame(mac_group)
        self.mac_list_frame.pack(fill=tk.BOTH, expand=True, pady=5)
        self._refresh_mac_for_selection()
        ttk.Button(mac_group, text="アダプタ再スキャン", command=self._refresh_mac_for_selection).pack(anchor=tk.W)

        # 動作モード
        debug_frame = ttk.LabelFrame(settings_frame, text="動作モード", padding="10")
        debug_frame.pack(fill=tk.X, pady=5)
        
        self.debug_mode_var = tk.BooleanVar(value=self.schedule_manager.debug_mode)
        cbtn = ttk.Checkbutton(debug_frame, 
                       text="デバッグモード（実際にシャットダウンしない）",
                       variable=self.debug_mode_var,
                       command=self._on_debug_mode_changed)
        cbtn.pack(anchor=tk.W)
        
        ttk.Label(debug_frame, 
                 text="※有効にすると、シャットダウンや再起動の代わりにログ出力のみ行います。\n"
                      "　ログは各タブのテキストボックスで確認できます。",
                 foreground="gray", font=("Meiryo UI", 9)).pack(anchor=tk.W, pady=(5,0))
                 


        # 免責事項 (Moved to Update Tab)
        # disclaimer_frame removed
        pass
                 

    def _setup_realtime_clock(self):
        """リアルタイム時計追尾のセットアップ"""
        self.realtime_vars = {
            # key: {"h_var": ActiveVar, "m_var": ActiveVar, "active": bool}
            # グループ化して管理
            "daily_s": {"h": self.daily_hour_var, "m": self.daily_minute_var, "active": not self.daily_enabled_var.get()},
            "weekly_s": {"h": self.weekly_add_hour_var, "m": self.weekly_add_minute_var, "active": True},
            "onetime_s": {"h": self.onetime_hour_var, "m": self.onetime_minute_var, "active": True},
            "daily_r": {"h": self.daily_hour_var_r, "m": self.daily_minute_var_r, "active": not self.daily_enabled_var_r.get()},
            "weekly_r": {"h": self.weekly_add_hour_var_r, "m": self.weekly_add_minute_var_r, "active": True},
            "onetime_r": {"h": self.onetime_hour_var_r, "m": self.onetime_minute_var_r, "active": True},
            # Startup Tab
            "daily_st": {"h": self.startup_hour_var, "m": self.startup_minute_var, "active": not self.startup_enabled_var.get()},
            "weekly_st": {"h": self.startup_weekly_add_hour_var, "m": self.startup_weekly_add_minute_var, "active": True},
            "onetime_st": {"h": self.startup_onetime_hour_var, "m": self.startup_onetime_minute_var, "active": True},
        }
        
        # 最初の更新
        self._update_realtime_vars()
        
    def _bind_realtime_stop(self, key, widget):
        """ウィジェット操作時に追尾を停止するイベントをバインド"""
        # ユーザーが一度でも時刻を変更したらすべてのリアルタイム追尾を解除
        
        def stop_all_tracking(event=None):
            for group in self.realtime_vars:
                self.realtime_vars[group]["active"] = False
                
        widget.bind("<FocusIn>", stop_all_tracking)
        widget.bind("<Button-1>", stop_all_tracking)
        widget.bind("<ButtonRelease-1>", stop_all_tracking)
        widget.bind("<Key>", stop_all_tracking)
        widget.bind("<MouseWheel>", stop_all_tracking)
        # Spinboxの矢印操作用
        widget.bind("<<Increment>>", stop_all_tracking)
        widget.bind("<<Decrement>>", stop_all_tracking)

    def _update_realtime_vars(self):
        """アクティブな変数を現在時刻に更新"""
        now = datetime.now()
        h_str = f"{now.hour:02d}"
        m_str = f"{now.minute:02d}"
        
        # Dailyの有効状態をチェックして追尾フラグを更新（まだユーザーが触っていない場合のみ）
        # ただし「一度でも変更したら(触ったら)」なので、有効化＝触ったとみなすべきか？
        # ここでは「無効」なら追尾、「有効」なら固定（保存値）とするのが自然。
        # ユーザーが「無効」のまま時刻をいじった場合 -> active=Falseになるので追尾止まる -> OK
        # ユーザーが「有効」にした場合 -> active=Falseにすべき（保存値を使うため） -> _on_daily_changedで制御
        
        for group, data in self.realtime_vars.items():
            if data["active"]:
                if data["h"].get() != h_str: data["h"].set(h_str)
                if data["m"].get() != m_str: data["m"].set(m_str)

    # --- イベントハンドラ (Shutdown) ---
    def _add_hours_later(self, hours):
        self.schedule_manager.add_onetime_hours_later(ACTION_SHUTDOWN, hours)
        self._update_schedule_display()
        t = datetime.now() + timedelta(hours=hours)
        self._log(f"{hours}時間後にシャットダウン予約: {t.strftime('%H:%M')}")
    def _remove_selected_quick(self):
        for i in self.quick_tree.selection():
            self.schedule_manager.remove_onetime(self.quick_tree.item(i)["tags"][0])
        self._update_schedule_display()
    def _on_daily_changed(self):
        try:
            e, h, m = self.daily_enabled_var.get(), int(self.daily_hour_var.get()), int(self.daily_minute_var.get())
            if e:
                # 有効化されたら追尾停止
                if "daily_s" in self.realtime_vars: self.realtime_vars["daily_s"]["active"] = False
                conflict = self.schedule_manager.check_conflict(ACTION_SHUTDOWN, "daily", {"hour":h, "minute":m})

                if conflict:
                     messagebox.showerror("エラー", f"競合するスケジュールがあります:\n{conflict}"); self.daily_enabled_var.set(False); return

            self.schedule_manager.daily_schedule[ACTION_SHUTDOWN] = {"enabled":e, "hour":h, "minute":m}
            self.schedule_manager.save()
            self._update_schedule_display()
        except: pass
    def _add_weekly(self):
        try:
            w=WEEKDAYS_JP.index(self.weekly_add_day_var.get()); h=int(self.weekly_add_hour_var.get()); m=int(self.weekly_add_minute_var.get())
            conflict = self.schedule_manager.check_conflict(ACTION_SHUTDOWN, "weekly", {"weekday":w, "hour":h, "minute":m})
            if conflict:
                messagebox.showerror("エラー", f"競合するスケジュールがあります:\n{conflict}"); return

            self.schedule_manager.add_weekly(ACTION_SHUTDOWN, w, h, m)
            self._update_schedule_display()
        except: pass
    def _remove_selected_weekly(self):
        for i in self.weekly_tree.selection(): self.schedule_manager.remove_weekly(self.weekly_tree.item(i)["tags"][0])
        self._update_schedule_display()
    def _add_onetime(self):
        try:
            y, mo, d, h, mi = int(self.onetime_year_var.get()), int(self.onetime_month_var.get()), int(self.onetime_day_var.get()), int(self.onetime_hour_var.get()), int(self.onetime_minute_var.get())
            dt_s = datetime(y, mo, d, h, mi).strftime("%Y-%m-%d %H:%M")
            conflict = self.schedule_manager.check_conflict(ACTION_SHUTDOWN, "onetime", {"datetime":dt_s})
            if conflict:
                messagebox.showerror("エラー", f"競合するスケジュールがあります:\n{conflict}"); return

            self.schedule_manager.add_onetime(ACTION_SHUTDOWN, dt_s)
            self._update_schedule_display()
        except: pass
    def _remove_selected_onetime(self):
        for i in self.onetime_tree.selection(): self.schedule_manager.remove_onetime(self.onetime_tree.item(i)["tags"][0])
        self._update_schedule_display()
    def _clear_executed_onetime(self):
        self.schedule_manager.clear_executed_onetime(ACTION_SHUTDOWN)
        self._update_schedule_display()
    def _cancel_all(self):
        """次回スケジュールをキャンセル（一回限り:削除、毎日/毎週:スキップ）"""
        try: subprocess.Popen(["shutdown", "/a"], creationflags=subprocess.CREATE_NO_WINDOW)
        except: pass
        
        if hasattr(self.schedule_manager, '_pending_active'):
             del self.schedule_manager._pending_active
        
        # 次回予定を取得してキャンセル処理
        dt, sched_type, action, sched_id = self.schedule_manager.get_next_event_info()
        if dt:
            dt_str = dt.strftime("%Y-%m-%d %H:%M")
            if sched_type == "onetime" and sched_id:
                # 一回限り: 削除
                self.schedule_manager.remove_onetime(sched_id)
                self._log_all(f"一回限りスケジュールを削除しました: {dt_str}")
            elif sched_type in ["weekly", "daily"]:
                # 毎週/毎日: その日時をスキップリストに追加
                self.schedule_manager.skipped_dates.append(dt_str)
                self.schedule_manager.save()
                self._log_all(f"スケジュールをスキップしました: {dt_str}")
        else:
            self._log_all("キャンセルしました")
        
        self._update_schedule_display()

    # --- イベントハンドラ (Restart) ---
    def _add_r_hours_later(self, hours):
        self.schedule_manager.add_onetime_hours_later(ACTION_RESTART, hours)
        self._update_schedule_display()
        t = datetime.now() + timedelta(hours=hours)
        self._log_r(f"{hours}時間後に再起動予約: {t.strftime('%H:%M')}")
    def _remove_selected_r_quick(self):
        for i in self.quick_tree_r.selection():
            self.schedule_manager.remove_onetime(self.quick_tree_r.item(i)["tags"][0])
        self._update_schedule_display()
    def _on_daily_r_changed(self):
        try:
            e, h, m = self.daily_enabled_var_r.get(), int(self.daily_hour_var_r.get()), int(self.daily_minute_var_r.get())
            if e:
                # 有効化されたら追尾停止
                if "daily_r" in self.realtime_vars: self.realtime_vars["daily_r"]["active"] = False
                conflict = self.schedule_manager.check_conflict(ACTION_RESTART, "daily", {"hour":h, "minute":m})

                if conflict:
                     messagebox.showerror("エラー", f"競合するスケジュールがあります:\n{conflict}"); self.daily_enabled_var_r.set(False); return

            self.schedule_manager.daily_schedule[ACTION_RESTART] = {"enabled":e, "hour":h, "minute":m}
            self.schedule_manager.save()
            self._update_schedule_display()
        except: pass
    def _add_weekly_r(self):
        try:
            w=WEEKDAYS_JP.index(self.weekly_add_day_var_r.get()); h=int(self.weekly_add_hour_var_r.get()); m=int(self.weekly_add_minute_var_r.get())
            conflict = self.schedule_manager.check_conflict(ACTION_RESTART, "weekly", {"weekday":w, "hour":h, "minute":m})
            if conflict:
                messagebox.showerror("エラー", f"競合するスケジュールがあります:\n{conflict}"); return

            self.schedule_manager.add_weekly(ACTION_RESTART, w, h, m)
            self._update_schedule_display()
        except: pass
    def _remove_selected_weekly_r(self):
        for i in self.weekly_tree_r.selection(): self.schedule_manager.remove_weekly(self.weekly_tree_r.item(i)["tags"][0])
        self._update_schedule_display()
    def _add_onetime_r(self):
        try:
            y, mo, d, h, mi = int(self.onetime_year_var_r.get()), int(self.onetime_month_var_r.get()), int(self.onetime_day_var_r.get()), int(self.onetime_hour_var_r.get()), int(self.onetime_minute_var_r.get())
            dt_s = datetime(y, mo, d, h, mi).strftime("%Y-%m-%d %H:%M")
            conflict = self.schedule_manager.check_conflict(ACTION_RESTART, "onetime", {"datetime":dt_s})
            if conflict:
                messagebox.showerror("エラー", f"競合するスケジュールがあります:\n{conflict}"); return
            self.schedule_manager.add_onetime(ACTION_RESTART, dt_s)
            self._update_schedule_display()
        except: pass
    def _remove_selected_onetime_r(self):
        for i in self.onetime_tree_r.selection(): self.schedule_manager.remove_onetime(self.onetime_tree_r.item(i)["tags"][0])
        self._update_schedule_display()
    def _clear_executed_onetime_r(self):
        self.schedule_manager.clear_executed_onetime(ACTION_RESTART)
        self._update_schedule_display()

    # --- 共通 ---
    def _update_schedule_display(self):
        # Shutdown Tab
        self._update_tree(self.quick_tree, self.weekly_tree, self.onetime_tree, ACTION_SHUTDOWN)
        # Restart Tab
        self._update_tree(self.quick_tree_r, self.weekly_tree_r, self.onetime_tree_r, ACTION_RESTART)
        
        # Next Event
        dt, sched_type, action, sched_id = self.schedule_manager.get_next_event_info()
        if dt:
            lbl = "シャットダウン" if action == ACTION_SHUTDOWN else "再起動"
            self.next_shutdown_var.set(f"{dt.strftime('%m/%d %H:%M')} ({lbl})")
        else:
            self.next_shutdown_var.set("スケジュールなし")

    def _update_tree(self, q_tree, w_tree, o_tree, action):
        for x in q_tree.get_children(): q_tree.delete(x)
        for x in w_tree.get_children(): w_tree.delete(x)
        for x in o_tree.get_children(): o_tree.delete(x)
        
        tomorrow = datetime.now() + timedelta(days=1)
        # OneTime/Quick
        for s in self.schedule_manager.onetime_schedules:
            if s["action"] != action: continue
            status = "実行済" if s.get("executed") else "待機"
            try:
                dt = datetime.strptime(s["datetime"], "%Y-%m-%d %H:%M")
                source = s.get("source", "manual")
                
                if source == "quick":
                     if not s.get("executed"):
                          q_tree.insert("", tk.END, values=(s["datetime"],), tags=(s["id"],))
                else:
                     o_tree.insert("", tk.END, values=(s["datetime"], status), tags=(s["id"],))
            except: pass
        # Weekly
        for s in self.schedule_manager.weekly_schedules:
            if s["action"] != action: continue
            w_tree.insert("", tk.END, values=(WEEKDAYS_JP[s["weekday"]], f"{s['hour']:02d}:{s['minute']:02d}"), tags=(s["id"],))

    def _log(self, msg):
        ts = datetime.now().strftime("%H:%M:%S")
        self.log_text.config(state="normal")
        self.log_text.insert(tk.END, f"[{ts}] {msg}\n")
        self.log_text.see(tk.END)
        self.log_text.config(state="disabled")

    def _log_r(self, msg):
        ts = datetime.now().strftime("%H:%M:%S")
        self.log_text_r.config(state="normal")
        self.log_text_r.insert(tk.END, f"[{ts}] {msg}\n")
        self.log_text_r.see(tk.END)
        self.log_text_r.config(state="disabled")

    def _log_all(self, msg):
        self._log(msg)
        self._log_r(msg)
    
    def _on_debug_mode_changed(self):
        self.schedule_manager.debug_mode = self.debug_mode_var.get()
        self.schedule_manager.save()
        self._log_all(f"デバッグモード: {self.schedule_manager.debug_mode}")

    def _check_for_updates(self):
        """アップデートを確認する"""
        self.check_update_btn.config(state="disabled")
        self.update_status_var.set("更新を確認中...")
        self.progress.pack(pady=10)
        self.progress.start()
        
        # 別スレッドで確認
        threading.Thread(target=self._update_check_worker, daemon=True).start()
    
    def _update_check_worker(self):
        try:
            # GitHub APIから最新リリース情報を取得
            req = urllib.request.Request(GITHUB_API_URL)
            req.add_header('User-Agent', 'SmartPowerManager')  # GitHub APIにはUAが必須
            
            with urllib.request.urlopen(req, timeout=10) as response:
                data = json.loads(response.read().decode("utf-8"))
            
            # タグ名（バージョン）取得
            tag_name = data.get("tag_name", "").lstrip("v")
            if not tag_name:
                raise Exception("バージョン情報を取得できませんでした")
            
            # アセット情報（EXEのURL）取得
            assets = data.get("assets", [])
            exe_asset = None
            for asset in assets:
                if asset["name"].endswith(".exe"):
                    exe_asset = asset
                    break
            
            if not exe_asset:
                raise Exception("リリースに実行ファイルが含まれていません")

            self.latest_release_info = {
                "version": tag_name,
                "url": exe_asset["browser_download_url"],
                "filename": exe_asset["name"]
            }
            
            def parse_version(v):
                return tuple(map(int, (v.lstrip("v").split("."))))

            if parse_version(tag_name) > parse_version(APP_VERSION):
                self.after(0, lambda: self._confirm_update(tag_name))
            else:
                self.after(0, lambda: self._update_ui_no_update(tag_name))
                
        except urllib.error.HTTPError as e:
            if e.code == 404:
                self.after(0, lambda: self._update_ui_error(
                    f"リポジトリまたは最新リリースが見つかりません。\n"
                    f"({GITHUB_USER}/{GITHUB_REPO})\n"
                    "インターネット接続やリポジトリ設定を確認してください。"
                ))
            elif e.code == 403:
                self.after(0, lambda: self._update_ui_error("APIレート制限です。しばらく待って再試行してください"))
            else:
                self.after(0, lambda: self._update_ui_error(f"HTTPエラー: {e.code}"))
        except Exception as e:
            self.after(0, lambda: self._update_ui_error(str(e)))
    
    def _update_ui_no_update(self, version):
        self.progress.stop()
        self.progress.pack_forget()
        self.check_update_btn.config(state="normal")
        self.update_status_var.set(f"お使いのバージョン ({APP_VERSION}) は最新です。")
        messagebox.showinfo("アップデート", "最新バージョンです。")
    
    def _update_ui_error(self, error_msg):
        self.progress.stop()
        self.progress.pack_forget()
        self.check_update_btn.config(state="normal")
        self.update_status_var.set("エラーが発生しました")
        messagebox.showerror("エラー", f"更新確認エラー: {error_msg}")

    def _confirm_update(self, latest_version):
        self.progress.stop()
        self.progress.pack_forget()
        msg = f"新しいバージョン v{latest_version} が利用可能です。\n今すぐ更新しますか？\n（更新後、アプリは自動的に再起動します）"
        if messagebox.askyesno("アップデート", msg):
            self._start_download()
        else:
            self.check_update_btn.config(state="normal")
            self.update_status_var.set("更新をキャンセルしました")
    
    def _start_download(self):
        self.update_status_var.set("新しいバージョンをダウンロード中...")
        self.progress.pack(pady=10)
        self.progress.start()
        threading.Thread(target=self._download_worker, daemon=True).start()
        
    def _download_worker(self):
        try:
            if not hasattr(self, 'latest_release_info'):
                raise Exception("リリース情報がありません")

            download_url = self.latest_release_info["url"]
            file_name = self.latest_release_info["filename"]
            
            current_exe = sys.executable
            download_dir = os.path.dirname(os.path.abspath(current_exe))
            target_path = os.path.join(download_dir, file_name)
            
            if os.path.abspath(target_path) == os.path.abspath(current_exe):
                target_path += ".new"

            with urllib.request.urlopen(download_url, timeout=60) as response:
                block_size = 8192
                with open(target_path, 'wb') as f:
                    while True:
                        buffer = response.read(block_size)
                        if not buffer:
                            break
                        f.write(buffer)
            
            self.after(0, lambda: self._execute_update(target_path))
            
        except Exception as e:
            self.after(0, lambda: self._update_ui_error(f"ダウンロード失敗: {e}"))

    def _execute_update(self, new_exe_path):
        """Rename-Swap方式で更新を実行（AV誤検知回避のため、バッチファイルを使わない）"""
        try:
            current_exe = sys.executable
            if not current_exe.lower().endswith(".exe"):
                messagebox.showwarning("開発モード", "Pythonスクリプト実行中は自動更新できません。")
                return

            current_dir = os.path.dirname(os.path.abspath(current_exe))
            current_name = os.path.basename(current_exe)
            
            # 1. 現在の実行ファイルをリネーム（Windowsでは実行中でもリネーム可能）
            #    PIDやタイムスタンプを付けてユニークにする
            old_name = f"{current_name}.{os.getpid()}.delete_me"
            old_path = os.path.join(current_dir, old_name)
            
            if os.path.exists(old_path):
                try: os.remove(old_path)
                except: pass # 万が一残っていたら消す努力をする
                
            os.rename(current_exe, old_path)
            
            # 2. 新しいファイルを本来の場所に移動
            #    new_exe_path が .new で終わっている場合などを考慮
            target_path = os.path.join(current_dir, current_name)
            
            if os.path.exists(target_path):
                 # リネーム後にまだファイルがある（謎）場合は消す
                 try: os.remove(target_path)
                 except: pass
            
            # shutil.move推奨だが、os.rename/replaceで十分
            # 異デバイス間移動の可能性も考慮して shutil.move を使うのが無難だが、
            # 同じフォルダ内のダウンロードなら rename でOK
            import shutil
            shutil.move(new_exe_path, target_path)
            
            # 3. 新しいアプリを起動
            subprocess.Popen([target_path])
            
            # 4. 自分は終了
            sys.exit(0)
            
        except Exception as e:
            self._update_ui_error(f"更新実行エラー: {e}")
            # 失敗した場合、リネームしたものを戻したいが、複雑になるのでエラー表示のみとする



    def _start_monitor(self):
        self.monitor_running = True
        self.monitor_thread = threading.Thread(target=self._monitor_loop, daemon=True)
        self.monitor_thread.start()
        self._log_all("スケジュール監視を開始しました")

    def _monitor_loop(self):
        last_minute = -1
        while self.monitor_running:
            now = datetime.now()
            
            # リアルタイム時計更新 (毎秒)
            self.after(0, self._update_realtime_vars)
            
            if now.minute != last_minute:
                last_minute = now.minute
                triggered = self.schedule_manager.check_and_execute(lambda m: self.after(0, lambda: self._log_all(m)))
                if triggered:
                    self.after(0, self._update_schedule_display)
                    if hasattr(self.schedule_manager, '_pending_active'):
                        self.after(100, self._show_confirm_dialog)
                self.after(0, lambda: None)
            time.sleep(2)

    def _show_confirm_dialog(self):
        if not hasattr(self.schedule_manager, '_pending_active'): return
        action = self.schedule_manager._pending_action
        trigger = self.schedule_manager._pending_trigger
        cb = self.schedule_manager._pending_callback
        del self.schedule_manager._pending_active
        
        # 画面がオフの場合に備えてディスプレイを起動
        wake_display()
        
        lbl = "シャットダウン" if action == ACTION_SHUTDOWN else "再起動"
        d = tk.Toplevel(self)
        d.title(f"{lbl}確認")
        d.geometry("350x150")
        
        # 常に最前面に表示
        d.attributes('-topmost', True)
        d.focus_force()
        
        ttk.Label(d, text=f"理由: {trigger}").pack(pady=10)
        cd = ttk.Label(d, text=f"60秒後に{lbl}します", font=("",12,"bold"))
        cd.pack()
        
        cancel = [False]
        def do_ex():
            reset_power_state()  # システム設定に戻す
            d.destroy()
            self._log_all(f"{lbl}実行")
            self.schedule_manager._do_immediate_action(action, cb)
        def do_cn():
            cancel[0] = True
            reset_power_state()  # システム設定に戻す
            d.destroy()
            self._log_all("キャンセル")
            
        f = ttk.Frame(d); f.pack(pady=10)
        ttk.Button(f, text="実行", command=do_ex).pack(side=tk.LEFT)
        ttk.Button(f, text="キャンセル", command=do_cn).pack(side=tk.LEFT)
        
        cnt = [60]
        def tick():
            if cancel[0]: return
            cnt[0] -= 1
            if cnt[0] <= 0: do_ex()
            else:
                cd.config(text=f"{cnt[0]}秒後に{lbl}します")
                d.after(1000, tick)
        d.after(1000, tick)

    def _check_disclaimer(self):
        """免責事項の確認（初回起動時）"""
        if self.schedule_manager.disclaimer_accepted:
            return

        # ダイアログウィンドウ作成
        dialog = tk.Toplevel(self)
        dialog.title("利用規約・免責事項")
        dialog.geometry("500x400")
        dialog.resizable(False, False)
        dialog.transient(self)
        dialog.grab_set()

        # 画面中央配置
        dialog.update_idletasks()
        try:
            x = (dialog.winfo_screenwidth() - 500) // 2
            y = (dialog.winfo_screenheight() - 400) // 2
            dialog.geometry(f"+{x}+{y}")
        except Exception:
            pass

        # タイトル
        try:
            ttk.Label(dialog, text="利用規約・免責事項", font=("Meiryo UI", 12, "bold")).pack(pady=10)
        except Exception:
            ttk.Label(dialog, text="利用規約・免責事項", font=("", 12, "bold")).pack(pady=10)

        # テキストエリア
        text_frame = ttk.Frame(dialog, padding=10)
        text_frame.pack(fill=tk.BOTH, expand=True)

        text_widget = tk.Text(text_frame, wrap=tk.WORD, height=10)
        text_widget.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        
        scrollbar = ttk.Scrollbar(text_frame, orient=tk.VERTICAL, command=text_widget.yview)
        scrollbar.pack(side=tk.RIGHT, fill=tk.Y)
        text_widget.config(yscrollcommand=scrollbar.set)

        disclaimer_text = (
            "本ソフトウェア（SmartPowerManager）を使用する前に、以下の免責事項をよくお読みください。\n\n"
            "1. 本ソフトウェアの使用により生じた、いかなる損害（データ消失、システム不具合、利益損失など）についても、"
            "開発者は一切の責任を負いません。\n\n"
            "2. 本ソフトウェアは、ユーザーの設定したスケジュールに従ってPCをシャットダウンします。"
            "未保存の作業がある場合、データが失われる可能性があります。\n\n"
            "3. 自動更新機能はGitHubの公開リポジトリを利用しています。\n\n"
            "本ソフトウェアを使用することで、上記に同意したものとみなされます。"
        )
        text_widget.insert(tk.END, disclaimer_text)
        text_widget.config(state="disabled")

        # 同意ボタン
        btn_frame = ttk.Frame(dialog, padding=10)
        btn_frame.pack(fill=tk.X)

        def on_accept():
            self.schedule_manager.disclaimer_accepted = True
            self.schedule_manager.save()
            dialog.destroy()

        def on_reject():
            sys.exit(0)

        ttk.Button(btn_frame, text="同意して開始", command=on_accept).pack(side=tk.RIGHT, padx=5)
        ttk.Button(btn_frame, text="同意しない（終了）", command=on_reject).pack(side=tk.RIGHT, padx=5)

        # ×ボタンでも終了
        dialog.protocol("WM_DELETE_WINDOW", on_reject)
        
        # ダイアログが閉じるまで待機
        self.wait_window(dialog)

    def _setup_styles(self):
        """スタイル設定"""
        style = ttk.Style()
        if "vista" in style.theme_names():
            style.theme_use("vista")
        
        default_font = ("Meiryo UI", 9)
        try:
            style.configure(".", font=default_font)
            style.configure("Treeview", font=default_font, rowheight=25)
            style.configure("Treeview.Heading", font=("Meiryo UI", 9, "bold"))
        except Exception:
            pass

    def _on_close(self):
        self.withdraw()

    def _create_icon_image(self):
        # オリジナルアイコンがあれば読み込む
        try:
            icon_path = resource_path("app_icon.png")
            if os.path.exists(icon_path):
                return Image.open(icon_path)
        except: pass
        
        # フォールバック: デフォルト生成
        width = 64
        height = 64
        color1 = "#1a73e8"
        color2 = "#ffffff"
        image = Image.new('RGB', (width, height), color1)
        dc = ImageDraw.Draw(image)
        dc.rectangle((width // 4, height // 4, width * 3 // 4, height * 3 // 4), fill=color2)
        return image

    def _run_tray(self):
        image = self._create_icon_image()
        def show_window(icon, item):
            self.after(0, self.deiconify)
            
        menu = pystray.Menu(
            pystray.MenuItem("表示", show_window),
            pystray.MenuItem("終了", self._quit_app)
        )
        self.tray_icon = pystray.Icon("SmartPowerManager", image, "SmartPowerManager", menu)
        self.tray_icon.title = "SmartPowerManager (実行中)"
        # Double click to show? pystray doesn't easily support double click events on all platforms,
        # but menu is standard.
        self.tray_icon.run()

    def _quit_app(self, icon=None, item=None):
        self.monitor_running = False
        if hasattr(self, 'tray_icon'):
            self.tray_icon.stop()
        self.destroy()
        sys.exit(0)

    def _check_startup_registry(self):
        """スタートアップフォルダにショートカットがあるか確認"""
        try:
            startup_path = os.path.join(os.getenv('APPDATA'), r'Microsoft\Windows\Start Menu\Programs\Startup')
            shortcut_path = os.path.join(startup_path, "SmartPowerManager.lnk")
            return os.path.exists(shortcut_path)
        except:
            return False

    def _clean_manual_startup(self):
        """旧レジストリ設定をクリーンアップ（互換性のため）"""
        try:
            # 旧バージョンのレジストリ設定を削除
            key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run", 0, winreg.KEY_WRITE)
            try:
                winreg.DeleteValue(key, "SmartPowerManager")
            except:
                pass
            winreg.CloseKey(key)
        except:
            pass

    def _clean_old_updates(self):
        """アップデート時に生成された一時ファイルを削除（exeの自動削除は無効化）"""
        current_dir = os.path.dirname(os.path.abspath(sys.executable))
        import glob
        
        # .delete_me の削除のみ実施
        for p in glob.glob(os.path.join(current_dir, "*.delete_me")):
            try: os.remove(p)
            except: pass

    def _cleanup_legacy_bat(self):
        """v1.7.1以前が生成した _update.bat が残っていたら削除する"""
        try:
            current_dir = os.path.dirname(os.path.abspath(sys.executable))
            bat_path = os.path.join(current_dir, "_update.bat")
            if os.path.exists(bat_path):
                os.remove(bat_path)
        except: pass

    def _toggle_startup(self):
        """スタートアップフォルダにショートカットを作成/削除"""
        try:
            startup_path = os.path.join(os.getenv('APPDATA'), r'Microsoft\Windows\Start Menu\Programs\Startup')
            shortcut_path = os.path.join(startup_path, "SmartPowerManager.lnk")
            
            if self.startup_registry_var.get():
                # ショートカットを作成
                app_path = sys.executable
                if not getattr(sys, 'frozen', False):
                    script_path = os.path.abspath(__file__)
                    target = f'"{app_path}" "{script_path}"'
                else:
                    target = f'"{app_path}"'
                
                # PowerShellでショートカット作成
                ps_script = f"""$WshShell = New-Object -ComObject WScript.Shell; $Shortcut = $WshShell.CreateShortcut('{shortcut_path}'); $Shortcut.TargetPath = '{app_path}'; $Shortcut.Save()"""
                subprocess.run(["powershell", "-Command", ps_script], 
                             creationflags=subprocess.CREATE_NO_WINDOW,
                             capture_output=True)
            else:
                # ショートカットを削除
                if os.path.exists(shortcut_path):
                    os.remove(shortcut_path)
        except Exception as e:
            messagebox.showerror("エラー", f"スタートアップ設定に失敗しました:\n{e}")

    def _ensure_startup_arg(self):
        """スタートアップ登録を修復・更新する（毎回実行してパスズレなどを直す）"""
        if not self.startup_registry_var.get(): return
        
        # 強制的に現在のパスで上書き登録
        app_path = sys.executable
        if not getattr(sys, 'frozen', False):
             script_path = os.path.abspath(__file__)
             cmd = f'"{app_path}" "{script_path}" --startup'
        else:
             cmd = f'"{app_path}" --startup'

        try:
            key = winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\CurrentVersion\Run", 0, winreg.KEY_WRITE)
            winreg.SetValueEx(key, "SmartPowerManager", 0, winreg.REG_SZ, cmd)
            winreg.CloseKey(key)
        except: pass

if __name__ == '__main__':
    # Log startup for debugging
    try:
        with open("debug.log", "a") as f:
            f.write(f"[{datetime.now()}] Started: {sys.executable} Args: {sys.argv}\n")
    except: pass

    # ゾンビプロセス（古いバージョンの残り）を強制終了
    # 自分以外の SmartPowerManager*.exe を全てkillする
    try:
        my_pid = os.getpid()
        subprocess.run(f'taskkill /F /IM SmartPowerManager*.exe /FI "PID ne {my_pid}"', 
                       shell=True, stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except: pass

    app = SmartPowerManagerApp()
    app.mainloop()
