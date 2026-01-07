# version: 1.4.3
# -*- coding: utf-8 -*-
"""
SmartPowerManager v1.4.3
PCのシャットダウンスケジュール管理アプリケーション

機能:
- 毎日/毎週/一回限りのシャットダウンスケジュール
- 毎週スケジュール: 各曜日ごとに個別設定可能
- x時間後シャットダウン: 1,3,6,9,12時間後を選択可能（削除可能）
- 優先順位: 一回限り > 毎週 > 毎日
- 自動起動タブ: MACアドレス表示（Pico W用）
- シャットダウン前確認ダイアログ（60秒カウントダウン）

v1.4.3 変更点:
- 起動エラーの修正
- アップデート機能の修正（リポジトリURL修正）
- エラーハンドリングの強化

v1.4.2 変更点:
- v1.4.1のアップデート不具合を修正
- 起動時のエラーを修正
- 不要なファイルの整理

v1.4.0 変更点:
- シャットダウン前に確認ダイアログを表示（60秒カウントダウン）
- 即時シャットダウンに変更
- 時刻Spinboxのラップアラウンド対応（00→59, 23→0）

v1.3.0 変更点:
- 2カラムレイアウトに変更（左: クイック設定/毎日/ログ、右: 毎週/一回限り）
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

# --- コンソールウィンドウを非表示にする (Windows用) ---
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
# 定数定義
# =============================================================================
APP_VERSION = "1.4.3"
APP_TITLE = "SmartPowerManager"  # アプリ名はシンプルに
CONFIG_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "schedules.json")
# GitHubのリポジトリ情報
GITHUB_USER = "kazu-1234"
GITHUB_REPO = "SmartPowerManager"
GITHUB_API_URL = f"https://api.github.com/repos/{GITHUB_USER}/{GITHUB_REPO}/releases/latest"

WEEKDAYS_JP = ["月曜日", "火曜日", "水曜日", "木曜日", "金曜日", "土曜日", "日曜日"]
WEEKDAYS_SHORT = ["月", "火", "水", "木", "金", "土", "日"]
HOURS_LATER_OPTIONS = [1, 3, 6, 9, 12]


# =============================================================================
# MACアドレス取得関数
# =============================================================================
def get_mac_addresses():
    """PCのMACアドレスを取得"""
    mac_list = []
    try:
        import csv
        import io
        # Windowsの場合 getmac コマンドを使用
        result = subprocess.run(
            ["getmac", "/v", "/fo", "csv"],
            capture_output=True, text=True, check=True,
            creationflags=subprocess.CREATE_NO_WINDOW,
            encoding='cp932'  # 日本語Windows用
        )
        reader = csv.reader(io.StringIO(result.stdout))
        header = next(reader, None)  # ヘッダー行をスキップ
        for row in reader:
            if len(row) >= 3:
                adapter_name = row[1].strip()  # アダプター名（2列目）
                mac = row[2].strip()           # MACアドレス（3列目）
                if mac and mac != "N/A":
                    # 区切りを-から:に変換
                    mac = mac.replace('-', ':')
                    mac_list.append({"name": adapter_name, "mac": mac})
    except Exception:
        pass
    
    # 代替手法: UUIDから取得（最低限1つは取得）
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
    """ Get absolute path to resource, works for dev and for PyInstaller """
    try:
        # PyInstaller creates a temp folder and stores path in _MEIPASS
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
        self.daily_schedule = {"enabled": False, "hour": 23, "minute": 0}
        self.weekly_schedules = []
        self.onetime_schedules = []
        self.debug_mode = False  # デフォルトはオフ
        self.disclaimer_accepted = False # 免責同意フラグ
        self.load()
    
    def load(self):
        """設定ファイルから読み込み"""
        if os.path.exists(self.config_path):
            try:
                with open(self.config_path, 'r', encoding='utf-8') as f:
                    data = json.load(f)
                    self.daily_schedule = data.get("daily", self.daily_schedule)
                    if "weekly" in data and isinstance(data["weekly"], dict):
                        old_weekly = data["weekly"]
                        if old_weekly.get("enabled", False):
                            self.weekly_schedules = [{
                                "id": str(uuid.uuid4()),
                                "weekday": old_weekly.get("weekday", 0),
                                "hour": old_weekly.get("hour", 23),
                                "minute": old_weekly.get("minute", 0)
                            }]
                    else:
                        self.weekly_schedules = data.get("weekly_schedules", [])
                    self.onetime_schedules = data.get("onetime", [])
                    self.debug_mode = data.get("debug_mode", True)
                    self.disclaimer_accepted = data.get("disclaimer_accepted", False)
            except Exception as e:
                print(f"設定の読み込みに失敗: {e}")
    
    def save(self):
        """設定ファイルに保存"""
        data = {
            "daily": self.daily_schedule,
            "weekly_schedules": self.weekly_schedules,
            "onetime": self.onetime_schedules,
            "debug_mode": self.debug_mode,
            "disclaimer_accepted": self.disclaimer_accepted
        }
        try:
            with open(self.config_path, 'w', encoding='utf-8') as f:
                json.dump(data, f, ensure_ascii=False, indent=2)
        except Exception as e:
            print(f"設定の保存に失敗: {e}")
    
    def add_weekly(self, weekday, hour, minute):
        schedule = {"id": str(uuid.uuid4()), "weekday": weekday, 
                   "hour": hour, "minute": minute}
        self.weekly_schedules.append(schedule)
        self.save()
        return schedule["id"]
    
    def remove_weekly(self, schedule_id):
        self.weekly_schedules = [s for s in self.weekly_schedules if s["id"] != schedule_id]
        self.save()
    
    def add_onetime(self, dt_str):
        schedule = {"id": str(uuid.uuid4()), "datetime": dt_str, "executed": False}
        self.onetime_schedules.append(schedule)
        self.save()
        return schedule["id"]
    
    def add_onetime_hours_later(self, hours):
        target_time = datetime.now() + timedelta(hours=hours)
        dt_str = target_time.strftime("%Y-%m-%d %H:%M")
        return self.add_onetime(dt_str)
    
    def remove_onetime(self, schedule_id):
        self.onetime_schedules = [s for s in self.onetime_schedules if s["id"] != schedule_id]
        self.save()
    
    def clear_executed_onetime(self):
        self.onetime_schedules = [s for s in self.onetime_schedules if not s.get("executed", False)]
        self.save()
    
    def get_next_shutdown_info(self):
        now = datetime.now()
        candidates = []
        
        for s in self.onetime_schedules:
            if s.get("executed", False):
                continue
            try:
                dt = datetime.strptime(s["datetime"], "%Y-%m-%d %H:%M")
                if dt > now:
                    candidates.append((dt, "onetime", s["id"]))
            except ValueError:
                pass
        
        for s in self.weekly_schedules:
            target_weekday = s["weekday"]
            target_time = now.replace(hour=s["hour"], minute=s["minute"], second=0, microsecond=0)
            days_ahead = target_weekday - now.weekday()
            if days_ahead < 0 or (days_ahead == 0 and target_time <= now):
                days_ahead += 7
            next_weekly = target_time + timedelta(days=days_ahead)
            candidates.append((next_weekly, "weekly", s["id"]))
        
        if self.daily_schedule["enabled"]:
            target_time = now.replace(
                hour=self.daily_schedule["hour"],
                minute=self.daily_schedule["minute"],
                second=0, microsecond=0
            )
            if target_time <= now:
                target_time += timedelta(days=1)
            candidates.append((target_time, "daily", None))
        
        if not candidates:
            return None, None, None
        candidates.sort(key=lambda x: x[0])
        return candidates[0]
    
    def check_and_execute(self, log_callback=None):
        now = datetime.now()
        current_weekday = now.weekday()
        
        for s in self.onetime_schedules:
            if s.get("executed", False):
                continue
            try:
                scheduled_dt = datetime.strptime(s["datetime"], "%Y-%m-%d %H:%M")
                if abs((now - scheduled_dt).total_seconds()) < 60:
                    s["executed"] = True
                    self.save()
                    self._execute_shutdown(f"一回限り ({s['datetime']})", log_callback)
                    return True
            except ValueError:
                pass
        
        for s in self.weekly_schedules:
            if (current_weekday == s["weekday"] and 
                now.hour == s["hour"] and now.minute == s["minute"]):
                self._execute_shutdown(
                    f"毎週 ({WEEKDAYS_JP[s['weekday']]} {s['hour']:02d}:{s['minute']:02d})",
                    log_callback
                )
                return True
        
        if self.daily_schedule["enabled"]:
            if now.hour == self.daily_schedule["hour"] and now.minute == self.daily_schedule["minute"]:
                self._execute_shutdown(
                    f"毎日 ({self.daily_schedule['hour']:02d}:{self.daily_schedule['minute']:02d})",
                    log_callback
                )
                return True
        return False
    
    def _execute_shutdown(self, trigger_type, log_callback=None):
        """シャットダウン実行（ダイアログ表示フラグをセット）"""
        msg = f"[{datetime.now().strftime('%Y-%m-%d %H:%M:%S')}] シャットダウン予定: {trigger_type}"
        if log_callback:
            log_callback(msg)
        if self.debug_mode:
            if log_callback:
                log_callback("[デバッグモード] 実際のシャットダウンはスキップされました")
            return
        
        # ダイアログ表示用のフラグとトリガー情報を保存
        self._pending_shutdown = True
        self._pending_trigger_type = trigger_type
        self._pending_log_callback = log_callback
    
    def _do_immediate_shutdown(self, trigger_type, log_callback=None):
        """即時シャットダウンを実行"""
        try:
            subprocess.Popen(
                ["shutdown", "/s", "/t", "0"],
                creationflags=subprocess.CREATE_NO_WINDOW
            )
            if log_callback:
                log_callback("シャットダウンコマンドを送信しました")
        except Exception as e:
            if log_callback:
                log_callback(f"シャットダウンコマンド実行エラー: {e}")


# =============================================================================
# メインGUIアプリケーション
# =============================================================================
class SmartPowerManagerApp(tk.Tk):
    """メインアプリケーションウィンドウ"""
    
    def __init__(self):
        super().__init__()
        self.title(APP_TITLE)
        self.geometry("900x650")
        self.minsize(900, 650)
        self.resizable(False, False)  # サイズ変更禁止
        
        self.schedule_manager = ScheduleManager()
        
        # 免責事項チェック
        self._check_disclaimer()
        
        # スタイル設定
        try:
            self._setup_styles()
        except:
            pass
        
        self.monitor_running = False
        self.monitor_thread = None
        
        self._setup_widgets()
        self._update_schedule_display()
        self._start_monitor()
        self.protocol("WM_DELETE_WINDOW", self._on_close)
    
    def _setup_widgets(self):
        main_frame = ttk.Frame(self, padding="10")
        main_frame.pack(fill=tk.BOTH, expand=True)
        
        notebook = ttk.Notebook(main_frame)
        notebook.pack(fill=tk.BOTH, expand=True)
        
        # シャットダウンタブ
        self.shutdown_tab = ttk.Frame(notebook, padding="10")
        notebook.add(self.shutdown_tab, text="シャットダウン")
        self._setup_shutdown_tab()
        
        # 自動起動タブ
        self.autoboot_tab = ttk.Frame(notebook, padding="10")
        notebook.add(self.autoboot_tab, text="自動起動")
        self._setup_autoboot_tab()
        
        # アップデートタブ
        self.update_tab = ttk.Frame(notebook, padding="10")
        notebook.add(self.update_tab, text="アップデート")
        self._setup_update_tab()
        
        # 設定タブ
        self.settings_tab = ttk.Frame(notebook, padding="10")
        notebook.add(self.settings_tab, text="設定")
        self._setup_settings_tab()
    
    def _setup_shutdown_tab(self):
        """シャットダウンタブ - 2カラムレイアウト"""
        # 2カラム用フレーム
        columns_frame = ttk.Frame(self.shutdown_tab)
        columns_frame.pack(fill=tk.BOTH, expand=True)
        columns_frame.columnconfigure(0, weight=1, uniform="col")  # 左カラム
        columns_frame.columnconfigure(1, weight=1, uniform="col")  # 右カラム
        columns_frame.rowconfigure(0, weight=1)
        
        # ============= 左カラム =============
        left_frame = ttk.Frame(columns_frame)
        left_frame.grid(row=0, column=0, sticky="nsew", padx=(0, 5))
        left_frame.rowconfigure(2, weight=1)  # ログが拡張
        
        # --- クイック設定 ---
        quick_frame = ttk.LabelFrame(left_frame, text="クイック設定：x時間後", padding="8")
        quick_frame.pack(fill=tk.X, pady=3)
        
        quick_btn_row = ttk.Frame(quick_frame)
        quick_btn_row.pack(fill=tk.X)
        for hours in HOURS_LATER_OPTIONS:
            btn = ttk.Button(quick_btn_row, text=f"{hours}h", width=4,
                           command=lambda h=hours: self._add_hours_later(h))
            btn.pack(side=tk.LEFT, padx=2, pady=2)
        
        # クイック設定一覧
        quick_list_frame = ttk.Frame(quick_frame)
        quick_list_frame.pack(fill=tk.BOTH, expand=True, pady=(5, 0))
        
        self.quick_tree = ttk.Treeview(quick_list_frame, columns=("datetime",), 
                                       show="headings", height=3)
        self.quick_tree.heading("datetime", text="予定日時")
        self.quick_tree.column("datetime", width=140)
        self.quick_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        
        quick_scroll = ttk.Scrollbar(quick_list_frame, orient=tk.VERTICAL,
                                    command=self.quick_tree.yview)
        self.quick_tree.configure(yscrollcommand=quick_scroll.set)
        quick_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        
        ttk.Button(quick_frame, text="選択を削除", 
                  command=self._remove_selected_quick).pack(anchor=tk.W, pady=(3, 0))
        
        # --- 毎日スケジュール ---
        daily_frame = ttk.LabelFrame(left_frame, text="毎日スケジュール", padding="8")
        daily_frame.pack(fill=tk.X, pady=3)
        
        daily_row = ttk.Frame(daily_frame)
        daily_row.pack(fill=tk.X)
        
        self.daily_enabled_var = tk.BooleanVar(
            value=self.schedule_manager.daily_schedule["enabled"]
        )
        ttk.Checkbutton(daily_row, text="有効", variable=self.daily_enabled_var,
                       command=self._on_daily_changed).pack(side=tk.LEFT)
        
        ttk.Label(daily_row, text="時刻:").pack(side=tk.LEFT, padx=(15, 5))
        
        self.daily_hour_var = tk.StringVar(
            value=f"{self.schedule_manager.daily_schedule['hour']:02d}"
        )
        ttk.Spinbox(daily_row, from_=0, to=23, width=3,
                   textvariable=self.daily_hour_var,
                   format="%02.0f", wrap=True,
                   command=self._on_daily_changed).pack(side=tk.LEFT)
        ttk.Label(daily_row, text=":").pack(side=tk.LEFT)
        
        self.daily_minute_var = tk.StringVar(
            value=f"{self.schedule_manager.daily_schedule['minute']:02d}"
        )
        ttk.Spinbox(daily_row, from_=0, to=59, width=3,
                   textvariable=self.daily_minute_var,
                   format="%02.0f", wrap=True,
                   command=self._on_daily_changed).pack(side=tk.LEFT)
        
        # --- 次回シャットダウン表示 + キャンセルボタン ---
        next_left_frame = ttk.LabelFrame(left_frame, text="次回シャットダウン", padding="8")
        next_left_frame.pack(fill=tk.X, pady=3)
        
        next_row = ttk.Frame(next_left_frame)
        next_row.pack(fill=tk.X)
        
        self.next_shutdown_var = tk.StringVar(value="スケジュールなし")
        ttk.Label(next_row, textvariable=self.next_shutdown_var).pack(side=tk.LEFT, anchor=tk.W)
        
        ttk.Button(next_row, text="キャンセル", 
                  command=self._cancel_shutdown).pack(side=tk.RIGHT, padx=(10, 0))
        
        # --- ログ ---
        log_frame = ttk.LabelFrame(left_frame, text="ログ", padding="5")
        log_frame.pack(fill=tk.BOTH, expand=True, pady=3)
        
        self.log_text = tk.Text(log_frame, height=8, state="disabled", wrap="word")
        log_scroll = ttk.Scrollbar(log_frame, orient=tk.VERTICAL, 
                                  command=self.log_text.yview)
        self.log_text.config(yscrollcommand=log_scroll.set)
        self.log_text.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        log_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        
        # ============= 右カラム =============
        right_frame = ttk.Frame(columns_frame)
        right_frame.grid(row=0, column=1, sticky="nsew", padx=(5, 0))
        right_frame.rowconfigure(0, weight=1)  # 毎週が拡張
        right_frame.rowconfigure(1, weight=1)  # 一回限りが拡張
        
        # --- 毎週スケジュール ---
        weekly_frame = ttk.LabelFrame(right_frame, text="毎週スケジュール", padding="8")
        weekly_frame.pack(fill=tk.BOTH, expand=True, pady=3)
        
        weekly_add_row = ttk.Frame(weekly_frame)
        weekly_add_row.pack(fill=tk.X, pady=(0, 5))
        
        self.weekly_add_day_var = tk.StringVar(value=WEEKDAYS_JP[0])
        ttk.Combobox(weekly_add_row, textvariable=self.weekly_add_day_var,
                    values=WEEKDAYS_JP, width=7, state="readonly").pack(side=tk.LEFT, padx=2)
        
        self.weekly_add_hour_var = tk.StringVar(value="23")
        ttk.Spinbox(weekly_add_row, from_=0, to=23, width=3,
                   textvariable=self.weekly_add_hour_var,
                   format="%02.0f", wrap=True).pack(side=tk.LEFT, padx=2)
        ttk.Label(weekly_add_row, text=":").pack(side=tk.LEFT)
        
        self.weekly_add_minute_var = tk.StringVar(value="00")
        ttk.Spinbox(weekly_add_row, from_=0, to=59, width=3,
                   textvariable=self.weekly_add_minute_var,
                   format="%02.0f", wrap=True).pack(side=tk.LEFT, padx=2)
        
        ttk.Button(weekly_add_row, text="追加", 
                  command=self._add_weekly).pack(side=tk.LEFT, padx=5)
        
        weekly_list_frame = ttk.Frame(weekly_frame)
        weekly_list_frame.pack(fill=tk.BOTH, expand=True)
        
        self.weekly_tree = ttk.Treeview(weekly_list_frame, 
                                        columns=("weekday", "time"), 
                                        show="headings", height=4)
        self.weekly_tree.heading("weekday", text="曜日")
        self.weekly_tree.heading("time", text="時刻")
        self.weekly_tree.column("weekday", width=80)
        self.weekly_tree.column("time", width=60)
        self.weekly_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        
        weekly_scroll = ttk.Scrollbar(weekly_list_frame, orient=tk.VERTICAL,
                                     command=self.weekly_tree.yview)
        self.weekly_tree.configure(yscrollcommand=weekly_scroll.set)
        weekly_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        
        ttk.Button(weekly_frame, text="選択を削除", 
                  command=self._remove_selected_weekly).pack(anchor=tk.W, pady=(3, 0))
        
        # --- 一回限りスケジュール ---
        onetime_frame = ttk.LabelFrame(right_frame, text="一回限り（最優先）", padding="8")
        onetime_frame.pack(fill=tk.BOTH, expand=True, pady=3)
        
        onetime_add_row = ttk.Frame(onetime_frame)
        onetime_add_row.pack(fill=tk.X, pady=(0, 5))
        
        current_year = datetime.now().year
        self.onetime_year_var = tk.StringVar(value=str(current_year))
        ttk.Spinbox(onetime_add_row, from_=current_year, to=2100, width=5,
                   textvariable=self.onetime_year_var).pack(side=tk.LEFT, padx=1)
        ttk.Label(onetime_add_row, text="/").pack(side=tk.LEFT)
        
        self.onetime_month_var = tk.StringVar(value=f"{datetime.now().month:02d}")
        ttk.Spinbox(onetime_add_row, from_=1, to=12, width=3,
                   textvariable=self.onetime_month_var,
                   format="%02.0f").pack(side=tk.LEFT, padx=1)
        ttk.Label(onetime_add_row, text="/").pack(side=tk.LEFT)
        
        self.onetime_day_var = tk.StringVar(value=f"{datetime.now().day:02d}")
        ttk.Spinbox(onetime_add_row, from_=1, to=31, width=3,
                   textvariable=self.onetime_day_var,
                   format="%02.0f").pack(side=tk.LEFT, padx=1)
        
        self.onetime_hour_var = tk.StringVar(value="23")
        ttk.Spinbox(onetime_add_row, from_=0, to=23, width=3,
                   textvariable=self.onetime_hour_var,
                   format="%02.0f", wrap=True).pack(side=tk.LEFT, padx=(5, 1))
        ttk.Label(onetime_add_row, text=":").pack(side=tk.LEFT)
        
        self.onetime_minute_var = tk.StringVar(value="00")
        ttk.Spinbox(onetime_add_row, from_=0, to=59, width=3,
                   textvariable=self.onetime_minute_var,
                   format="%02.0f", wrap=True).pack(side=tk.LEFT, padx=1)
        
        ttk.Button(onetime_add_row, text="追加", 
                  command=self._add_onetime).pack(side=tk.LEFT, padx=5)
        
        onetime_list_frame = ttk.Frame(onetime_frame)
        onetime_list_frame.pack(fill=tk.BOTH, expand=True)
        
        self.onetime_tree = ttk.Treeview(onetime_list_frame, 
                                         columns=("datetime", "status"), 
                                         show="headings", height=4)
        self.onetime_tree.heading("datetime", text="日時")
        self.onetime_tree.heading("status", text="状態")
        self.onetime_tree.column("datetime", width=120)
        self.onetime_tree.column("status", width=60)
        self.onetime_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        
        onetime_scroll = ttk.Scrollbar(onetime_list_frame, orient=tk.VERTICAL,
                                      command=self.onetime_tree.yview)
        self.onetime_tree.configure(yscrollcommand=onetime_scroll.set)
        onetime_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        
        onetime_btn_row = ttk.Frame(onetime_frame)
        onetime_btn_row.pack(fill=tk.X, pady=(3, 0))
        ttk.Button(onetime_btn_row, text="選択を削除", 
                  command=self._remove_selected_onetime).pack(side=tk.LEFT)
        ttk.Button(onetime_btn_row, text="実行済み削除", 
                  command=self._clear_executed_onetime).pack(side=tk.LEFT, padx=5)
        
        # 次回シャットダウン表示は左側に移動済み
    
    def _setup_autoboot_tab(self):
        """自動起動タブの設定"""
        info_frame = ttk.Frame(self.autoboot_tab)
        info_frame.pack(fill=tk.X, pady=10)
        
        ttk.Label(info_frame, text="🔧 自動起動機能",
                 font=("", 14, "bold")).pack(anchor=tk.W)
        ttk.Label(info_frame, 
                 text="Raspberry Pi Pico W を使用してPCの自動起動を行います。\n"
                      "以下のMACアドレスをPico W側で設定してください。",
                 justify=tk.LEFT).pack(anchor=tk.W, pady=(5, 0))
        
        mac_frame = ttk.LabelFrame(self.autoboot_tab, 
                                   text="このPCのMACアドレス", padding="10")
        mac_frame.pack(fill=tk.X, pady=10)
        
        mac_list = get_mac_addresses()
        if mac_list:
            for mac_info in mac_list:
                row_frame = ttk.Frame(mac_frame)
                row_frame.pack(fill=tk.X, pady=2)
                ttk.Label(row_frame, text=f"{mac_info['name']}:", 
                         width=25, anchor=tk.W).pack(side=tk.LEFT)
                mac_entry = ttk.Entry(row_frame, width=20)
                mac_entry.insert(0, mac_info['mac'])
                mac_entry.config(state="readonly")
                mac_entry.pack(side=tk.LEFT, padx=5)
                
                def make_copy_func(mac):
                    def copy_mac():
                        self.clipboard_clear()
                        self.clipboard_append(mac)
                        self._log(f"MACアドレスをコピーしました: {mac}")
                    return copy_mac
                ttk.Button(row_frame, text="コピー", width=6,
                          command=make_copy_func(mac_info['mac'])).pack(side=tk.LEFT)
        else:
            ttk.Label(mac_frame, text="MACアドレスを取得できませんでした",
                     foreground="red").pack(anchor=tk.W)
        
        ttk.Button(mac_frame, text="再取得", 
                  command=self._refresh_mac_addresses).pack(anchor=tk.W, pady=(10, 0))
        self.mac_frame = mac_frame
        
        pico_frame = ttk.LabelFrame(self.autoboot_tab, 
                                    text="Raspberry Pi Pico W 設定", padding="10")
        pico_frame.pack(fill=tk.X, pady=10)
        ttk.Label(pico_frame, 
                 text="【設定手順】\n"
                      "1. 上記のMACアドレスをメモまたはコピー\n"
                      "2. Pico Wのコードに対象MACアドレスを設定\n"
                      "3. 起動スケジュールをPico W側で設定",
                 justify=tk.LEFT).pack(anchor=tk.W)
    
    def _refresh_mac_addresses(self):
        for widget in self.mac_frame.winfo_children():
            widget.destroy()
        
        mac_list = get_mac_addresses()
        if mac_list:
            for mac_info in mac_list:
                row_frame = ttk.Frame(self.mac_frame)
                row_frame.pack(fill=tk.X, pady=2)
                ttk.Label(row_frame, text=f"{mac_info['name']}:", 
                         width=25, anchor=tk.W).pack(side=tk.LEFT)
                mac_entry = ttk.Entry(row_frame, width=20)
                mac_entry.insert(0, mac_info['mac'])
                mac_entry.config(state="readonly")
                mac_entry.pack(side=tk.LEFT, padx=5)
                
                def make_copy_func(mac):
                    def copy_mac():
                        self.clipboard_clear()
                        self.clipboard_append(mac)
                        self._log(f"MACアドレスをコピーしました: {mac}")
                    return copy_mac
                ttk.Button(row_frame, text="コピー", width=6,
                          command=make_copy_func(mac_info['mac'])).pack(side=tk.LEFT)
        else:
            ttk.Label(self.mac_frame, text="MACアドレスを取得できませんでした",
                     foreground="red").pack(anchor=tk.W)
        
        ttk.Button(self.mac_frame, text="再取得", 
                  command=self._refresh_mac_addresses).pack(anchor=tk.W, pady=(10, 0))
        self._log("MACアドレスを再取得しました")
    
    def _setup_update_tab(self):
        """アップデートタブ - 自動更新機能"""
        frame = ttk.Frame(self.update_tab, padding="20")
        frame.pack(fill=tk.BOTH, expand=True)
        
        # タイトル
        # フォントを画像（マイブーム）の雰囲気に合わせる（Meiryo UI, Gothic系）
        custom_font = ("Meiryo UI", 14, "bold")
        normal_font = ("Meiryo UI", 11)
        
        ttk.Label(frame, text="📦 自動アップデート",
                 font=custom_font).pack(pady=(0, 20))
        
        # 現在のバージョン
        ttk.Label(frame, text=f"現在のバージョン: v{APP_VERSION}",
                 font=normal_font).pack(pady=5)
                 
        # ステータス表示
        self.update_status_var = tk.StringVar(value="ボタンを押して更新を確認してください")
        status_label = ttk.Label(frame, textvariable=self.update_status_var,
                                foreground="blue", padding=10, font=normal_font)
        status_label.pack(pady=10)
        
        # 更新確認ボタン
        self.check_update_btn = ttk.Button(frame, text="アップデートを確認",
                                         command=self._check_for_updates)
        self.check_update_btn.pack(pady=10)
        
        # プログレスバー
        self.progress = ttk.Progressbar(frame, mode="indeterminate", length=300)
    
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
            
            # タグ名（バージョン）取得 (例: "v1.4.1" -> "1.4.1")
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
                # EXEが見つからない場合はエラーではなく「最新」扱いにするか、警告
                # ここではエラーとして扱う
                raise Exception("リリースに実行ファイルが含まれていません")

            self.latest_release_info = {
                "version": tag_name,
                "url": exe_asset["browser_download_url"],
                "filename": exe_asset["name"]
            }
            
            # バージョン比較関数
            def parse_version(v):
                return tuple(map(int, (v.split("."))))

            # バージョン比較
            if parse_version(tag_name) > parse_version(APP_VERSION):
                self.after(0, lambda: self._confirm_update(tag_name))
            else:
                self.after(0, lambda: self._update_ui_no_update(tag_name))
                
                
        except urllib.error.HTTPError as e:
            if e.code == 404:
                # リポジトリまたはリリースが見つからない
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
        self.update_status_var.set(f"お使いのバージョン (v{APP_VERSION}) は最新です。")
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
            
            # 実行ファイルのパスを取得
            current_exe = sys.executable
            download_dir = os.path.dirname(os.path.abspath(current_exe))
            # 一時ファイルとしてではなく、正しいファイル名で保存
            target_path = os.path.join(download_dir, file_name)
            
            # 既に同名ファイルがある場合は上書き（実行中でなければ）
            # ただし自分自身と同じ名前なら一時名にする必要があるが、
            # バージョンが変わればファイル名も変わるはずなので競合しないはず
            if os.path.abspath(target_path) == os.path.abspath(current_exe):
                target_path += ".new"

            # EXEをダウンロード（チャンク読み込み）
            with urllib.request.urlopen(download_url, timeout=60) as response:
                # total_size = int(response.getheader('Content-Length', 0))
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
        """バッチファイルを作成して更新を実行"""
        try:
            current_exe = sys.executable
            if not current_exe.lower().endswith(".exe"):
                self.progress.stop()
                self.progress.pack_forget()
                self.check_update_btn.config(state="normal")
                messagebox.showwarning("開発モード", "Pythonスクリプト実行中は自動更新できません。\nダウンロードは完了しました。")
                self.update_status_var.set("ダウンロード完了（更新スキップ）")
                return

            current_exe_name = os.path.basename(current_exe)
            new_exe_name = os.path.basename(new_exe_path)
            batch_file = os.path.join(os.path.dirname(current_exe), "_update.bat")
            
            # バッチファイル内容
            # 1. 待機
            # 2. 古いEXE削除
            # 3. 新しいEXE起動
            # 4. バッチ削除
            # ※新ファイルが .new で終わる場合（同名更新）はリネームが必要だが、
            #   基本は別名（バージョン違い）なのでリネーム不要
            
            rename_cmd = ""
            if new_exe_path.endswith(".new"):
                real_new_name = new_exe_name[:-4] # .new削除
                rename_cmd = f'move /y "{new_exe_name}" "{real_new_name}"\nset "new_exe_name={real_new_name}"'
                start_target = real_new_name
            else:
                start_target = new_exe_name
                
            batch_content = f"""@echo off
timeout /t 2 /nobreak >nul
del "{current_exe_name}"
{rename_cmd}
start "" "{start_target}"
del "%~f0"
"""
            with open(batch_file, "w", encoding="cp932") as f:
                f.write(batch_content)
            
            # バッチ実行して終了
            # CREATE_NEW_CONSOLE (0x00000010) を使用して独立したコンソールで実行
            CREATE_NEW_CONSOLE = 0x00000010
            subprocess.Popen(
                ["cmd.exe", "/c", batch_file],
                creationflags=CREATE_NEW_CONSOLE
            )
            # プロセス起動を確実にするため少し待機してから終了
            self.after(1000, self.quit)
            
        except Exception as e:
            self._update_ui_error(f"更新実行エラー: {e}")
    
    def _setup_settings_tab(self):
        settings_frame = ttk.Frame(self.settings_tab)
        settings_frame.pack(fill=tk.BOTH, expand=True)
        
        # フォント設定
        title_font = ("Meiryo UI", 10, "bold")
        normal_font = ("Meiryo UI", 9)
        
        debug_frame = ttk.LabelFrame(settings_frame, text="動作モード", padding="10")
        debug_frame.pack(fill=tk.X, pady=5)
        
        self.debug_mode_var = tk.BooleanVar(value=self.schedule_manager.debug_mode)
        # スタイル適用のためttk.Checkbuttonを使用するが、フォント指定はstyleが必要。
        # 個別に指定できない場合もあるが、ラベルには適用可能。
        
        # Checkbuttonのフォント変更はStyle設定が必要だが、簡易的に親フレームのフォントに依存させるか、
        # ここではCheckbutton自体のフォントはデフォルトのまま（Meiryo UIに近い）でも良いが、
        # 一応統一を試みる。TkinterのStandard Checkbuttonを使う手もあるがttkで統一。
        
        cbtn = ttk.Checkbutton(debug_frame, 
                       text="デバッグモード（実際にシャットダウンしない）",
                       variable=self.debug_mode_var,
                       command=self._on_debug_mode_changed)
        cbtn.pack(anchor=tk.W)
        # cbtn.configure(font=normal_font) # ttkでは効かない場合がある
        
        ttk.Label(debug_frame, 
                 text="※ 初回使用時はデバッグモードを有効にして動作確認することをお勧めします。",
                 foreground="gray", font=normal_font).pack(anchor=tk.W, pady=(5, 0))
        
        version_frame = ttk.LabelFrame(settings_frame, text="バージョン情報", padding="10")
        version_frame.pack(fill=tk.X, pady=5)
        ttk.Label(version_frame, text=f"{APP_TITLE} v{APP_VERSION}",
                 font=title_font).pack(anchor=tk.W)
        ttk.Label(version_frame, 
                 text="PCシャットダウンスケジュール管理アプリ", font=normal_font).pack(anchor=tk.W)
        
        # 免責事項
        disclaimer_frame = ttk.LabelFrame(settings_frame, text="免責事項", padding="10")
        disclaimer_frame.pack(fill=tk.X, pady=5)
        disclaimer_text = (
            "本ソフトウェアの使用により生じた損害（データ消失など）について、\n"
            "開発者は一切の責任を負いません。自己責任でご使用ください。"
        )
        ttk.Label(disclaimer_frame, text=disclaimer_text, 
                 justify=tk.LEFT, foreground="#555555", font=("Meiryo UI", 8)).pack(anchor=tk.W)
    
    # =========================================================================
    # イベントハンドラ
    # =========================================================================
    def _add_hours_later(self, hours):
        self.schedule_manager.add_onetime_hours_later(hours)
        self._update_schedule_display()
        target_time = datetime.now() + timedelta(hours=hours)
        self._log(f"{hours}時間後にシャットダウン予約: {target_time.strftime('%H:%M')}")
    
    def _remove_selected_quick(self):
        """クイック設定から選択を削除"""
        selected = self.quick_tree.selection()
        if not selected:
            messagebox.showinfo("情報", "削除するスケジュールを選択してください")
            return
        for item in selected:
            schedule_id = self.quick_tree.item(item)["tags"][0]
            self.schedule_manager.remove_onetime(schedule_id)
        self._update_schedule_display()
        self._log("クイック設定を削除しました")
    
    def _on_daily_changed(self):
        try:
            self.schedule_manager.daily_schedule["enabled"] = self.daily_enabled_var.get()
            self.schedule_manager.daily_schedule["hour"] = int(self.daily_hour_var.get())
            self.schedule_manager.daily_schedule["minute"] = int(self.daily_minute_var.get())
            self.schedule_manager.save()
            self._update_schedule_display()
        except ValueError:
            pass
    
    def _add_weekly(self):
        try:
            weekday_name = self.weekly_add_day_var.get()
            if weekday_name not in WEEKDAYS_JP:
                return
            weekday = WEEKDAYS_JP.index(weekday_name)
            hour = int(self.weekly_add_hour_var.get())
            minute = int(self.weekly_add_minute_var.get())
            self.schedule_manager.add_weekly(weekday, hour, minute)
            self._update_schedule_display()
            self._log(f"毎週スケジュール追加: {weekday_name} {hour:02d}:{minute:02d}")
        except ValueError as e:
            messagebox.showerror("エラー", f"無効な時刻です: {e}")
    
    def _remove_selected_weekly(self):
        selected = self.weekly_tree.selection()
        if not selected:
            messagebox.showinfo("情報", "削除するスケジュールを選択してください")
            return
        for item in selected:
            schedule_id = self.weekly_tree.item(item)["tags"][0]
            self.schedule_manager.remove_weekly(schedule_id)
        self._update_schedule_display()
        self._log("毎週スケジュールを削除しました")
    
    def _add_onetime(self):
        try:
            year = int(self.onetime_year_var.get())
            month = int(self.onetime_month_var.get())
            day = int(self.onetime_day_var.get())
            hour = int(self.onetime_hour_var.get())
            minute = int(self.onetime_minute_var.get())
            dt = datetime(year, month, day, hour, minute)
            if dt <= datetime.now():
                messagebox.showwarning("警告", "過去の日時は設定できません")
                return
            dt_str = dt.strftime("%Y-%m-%d %H:%M")
            self.schedule_manager.add_onetime(dt_str)
            self._update_schedule_display()
            self._log(f"一回限りスケジュール追加: {dt_str}")
        except ValueError as e:
            messagebox.showerror("エラー", f"無効な日時です: {e}")
    
    def _remove_selected_onetime(self):
        selected = self.onetime_tree.selection()
        if not selected:
            messagebox.showinfo("情報", "削除するスケジュールを選択してください")
            return
        for item in selected:
            schedule_id = self.onetime_tree.item(item)["tags"][0]
            self.schedule_manager.remove_onetime(schedule_id)
        self._update_schedule_display()
        self._log("一回限りスケジュールを削除しました")
    
    def _cancel_shutdown(self):
        """予定されているシャットダウンをキャンセル"""
        try:
            subprocess.Popen(
                ["shutdown", "/a"],
                creationflags=subprocess.CREATE_NO_WINDOW
            )
            self._log("シャットダウンをキャンセルしました")
        except Exception as e:
            self._log(f"キャンセルエラー: {e}")
    
    def _clear_executed_onetime(self):
        self.schedule_manager.clear_executed_onetime()
        self._update_schedule_display()
        self._log("実行済みスケジュールを削除しました")
    
    def _on_debug_mode_changed(self):
        self.schedule_manager.debug_mode = self.debug_mode_var.get()
        self.schedule_manager.save()
        mode_str = "有効" if self.schedule_manager.debug_mode else "無効"
        self._log(f"デバッグモードを{mode_str}にしました")
    
    # =========================================================================
    # 表示更新
    # =========================================================================
    def _update_schedule_display(self):
        # クイック設定（一回限りの中で今日〜明日のもの）
        for item in self.quick_tree.get_children():
            self.quick_tree.delete(item)
        
        now = datetime.now()
        tomorrow = now + timedelta(days=1)
        for s in self.schedule_manager.onetime_schedules:
            if s.get("executed", False):
                continue
            try:
                dt = datetime.strptime(s["datetime"], "%Y-%m-%d %H:%M")
                if dt <= tomorrow.replace(hour=23, minute=59):
                    self.quick_tree.insert("", tk.END, values=(s["datetime"],), tags=(s["id"],))
            except ValueError:
                pass
        
        # 毎週リスト
        for item in self.weekly_tree.get_children():
            self.weekly_tree.delete(item)
        for s in self.schedule_manager.weekly_schedules:
            weekday_name = WEEKDAYS_JP[s["weekday"]]
            time_str = f"{s['hour']:02d}:{s['minute']:02d}"
            self.weekly_tree.insert("", tk.END, values=(weekday_name, time_str), tags=(s["id"],))
        
        # 一回限りリスト
        for item in self.onetime_tree.get_children():
            self.onetime_tree.delete(item)
        for s in self.schedule_manager.onetime_schedules:
            status = "実行済み" if s.get("executed", False) else "待機中"
            self.onetime_tree.insert("", tk.END, values=(s["datetime"], status), tags=(s["id"],))
        
        # 次回シャットダウン
        next_dt, next_type, _ = self.schedule_manager.get_next_shutdown_info()
        if next_dt:
            type_names = {"onetime": "一回限り", "weekly": "毎週", "daily": "毎日"}
            type_name = type_names.get(next_type, next_type)
            self.next_shutdown_var.set(f"{next_dt.strftime('%Y-%m-%d %H:%M')} ({type_name})")
        else:
            self.next_shutdown_var.set("スケジュールなし")
    
    def _log(self, message):
        timestamp = datetime.now().strftime("%H:%M:%S")
        self.log_text.config(state="normal")
        self.log_text.insert(tk.END, f"[{timestamp}] {message}\n")
        self.log_text.see(tk.END)
        self.log_text.config(state="disabled")
    
    # =========================================================================
    # メニューバー
    # =========================================================================
    def _create_menu(self):
        """メニューバーを作成"""
        menubar = tk.Menu(self.root)
        self.root.config(menu=menubar)
        
        file_menu = tk.Menu(menubar, tearoff=0)
        menubar.add_cascade(label="ファイル", menu=file_menu)
        file_menu.add_command(label="設定を保存", command=self.schedule_manager.save)
        file_menu.add_separator()
        file_menu.add_command(label="終了", command=self._on_close) # Changed from on_closing to _on_close
        
        help_menu = tk.Menu(menubar, tearoff=0)
        menubar.add_cascade(label="ヘルプ", menu=help_menu)
        help_menu.add_command(label="GitHubを開く", command=self._open_github)
        help_menu.add_separator()
        help_menu.add_command(label="バージョン情報", command=self._show_version)

    def _open_github(self):
        """GitHubリポジトリをブラウザで開く"""
        import webbrowser
        webbrowser.open("https://github.com/kazu-1234/-SmartPowerManager")

    def _show_version(self):
        """バージョン情報を表示"""
        messagebox.showinfo("バージョン情報", 
                          f"{APP_TITLE} v{APP_VERSION}\n\n"
                          "© 2026 SmartPowerManager Project\n"
                          "Powered by Python & Tkinter")

    def _check_disclaimer(self):
        """初回起動時に免責事項への同意を確認"""
        if not self.schedule_manager.disclaimer_accepted:
            msg = (
                "【利用規約・免責事項】\n\n"
                "本ソフトウェアの使用により生じた損害（データ消失、ハードウェア故障など）について、"
                "開発者は一切の責任を負いません。\n\n"
                "本ソフトウェアを使用するには、上記に同意する必要があります。\n"
                "同意しますか？"
            )
            # yes/noで同意確認
            if messagebox.askyesno("利用規約・免責事項の確認", msg, icon='warning'):
                self.schedule_manager.disclaimer_accepted = True
                self.schedule_manager.save()
            else:
                messagebox.showinfo("終了", "同意されなかったため、アプリケーションを終了します。")
                self.root.destroy()
                sys.exit()

    # =========================================================================
    # 監視スレッド
    # =========================================================================
    def _start_monitor(self):
        self.monitor_running = True
        self.monitor_thread = threading.Thread(target=self._monitor_loop, daemon=True)
        self.monitor_thread.start()
        self._log("スケジュール監視を開始しました")

    
    def _monitor_loop(self):
        last_check_minute = -1
        while self.monitor_running:
            now = datetime.now()
            current_minute = now.minute
            if current_minute != last_check_minute:
                last_check_minute = current_minute
                def log_callback(msg):
                    self.after(0, lambda m=msg: self._log(m))
                triggered = self.schedule_manager.check_and_execute(log_callback)
                if triggered:
                    self.after(0, self._update_schedule_display)
                    # シャットダウン確認ダイアログを表示
                    if hasattr(self.schedule_manager, '_pending_shutdown') and \
                       self.schedule_manager._pending_shutdown:
                        self.after(100, self._show_shutdown_confirm_dialog)
                        self.schedule_manager._pending_shutdown = False
                self.after(0, self._update_status)
            time.sleep(5)
    
    def _show_shutdown_confirm_dialog(self):
        """シャットダウン確認ダイアログを表示（60秒タイムアウト付き）"""
        trigger_type = getattr(self.schedule_manager, '_pending_trigger_type', '')
        log_callback = getattr(self.schedule_manager, '_pending_log_callback', None)
        
        # カスタムダイアログを作成（タイムアウト付き）
        dialog = tk.Toplevel(self)
        dialog.title("シャットダウン確認")
        dialog.geometry("350x150")
        dialog.resizable(False, False)
        dialog.transient(self)
        dialog.grab_set()
        
        # 画面中央に配置
        dialog.update_idletasks()
        x = (dialog.winfo_screenwidth() - 350) // 2
        y = (dialog.winfo_screenheight() - 150) // 2
        dialog.geometry(f"+{x}+{y}")
        
        # 残り時間
        remaining = tk.IntVar(value=60)
        cancelled = [False]  # リストで参照を保持
        
        # メッセージ
        ttk.Label(dialog, text=f"スケジュール: {trigger_type}", 
                 font=("", 10)).pack(pady=(15, 5))
        countdown_label = ttk.Label(dialog, text="60秒後にシャットダウンします",
                                   font=("", 11, "bold"))
        countdown_label.pack(pady=5)
        
        # ボタンフレーム
        btn_frame = ttk.Frame(dialog)
        btn_frame.pack(pady=15)
        
        def do_shutdown():
            dialog.destroy()
            self._log("シャットダウンを実行します")
            self.schedule_manager._do_immediate_shutdown(trigger_type, log_callback)
        
        def cancel_shutdown():
            cancelled[0] = True
            dialog.destroy()
            self._log("シャットダウンをキャンセルしました")
        
        ttk.Button(btn_frame, text="シャットダウン", 
                  command=do_shutdown).pack(side=tk.LEFT, padx=10)
        ttk.Button(btn_frame, text="キャンセル", 
                  command=cancel_shutdown).pack(side=tk.LEFT, padx=10)
        
        # カウントダウン
        def countdown():
            if cancelled[0] or not dialog.winfo_exists():
                return
            r = remaining.get() - 1
            remaining.set(r)
            if r <= 0:
                do_shutdown()
            else:
                countdown_label.config(text=f"{r}秒後にシャットダウンします")
                dialog.after(1000, countdown)
        
        dialog.after(1000, countdown)
        
        # ダイアログが閉じられた時の処理
        dialog.protocol("WM_DELETE_WINDOW", cancel_shutdown)
    
    def _update_status(self):
        # ステータスバー削除のため空実装
        pass
    
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
            # フォントがない場合
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
        self.monitor_running = False
        if self.monitor_thread:
            self.monitor_thread.join(timeout=1)
        self.destroy()


# =============================================================================
# メイン
# =============================================================================
if __name__ == '__main__':
    app = SmartPowerManagerApp()
    app.mainloop()
