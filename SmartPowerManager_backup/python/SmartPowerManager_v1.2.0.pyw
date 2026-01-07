# version: 1.2.0
# -*- coding: utf-8 -*-
"""
SmartPowerManager v1.2.0
PCのシャットダウンスケジュール管理アプリケーション

機能:
- 毎日/毎週/一回限りのシャットダウンスケジュール
- 毎週スケジュール: 各曜日ごとに個別設定可能
- x時間後シャットダウン: 1,3,6,9,12時間後を選択可能
- 優先順位: 一回限り > 毎週 > 毎日
- 自動起動タブ: MACアドレス表示（Pico W用）
- 将来実装: Pico W自動起動、GitHubアップデート

v1.2.0 変更点:
- 「x時間後にシャットダウン」機能を追加（1,3,6,9,12時間から選択）

v1.1.0 変更点:
- 毎週スケジュールを曜日別に設定可能に変更
- 自動起動タブにMACアドレス表示を追加
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
APP_VERSION = "1.2.0"
APP_TITLE = f"SmartPowerManager v{APP_VERSION}"
CONFIG_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "schedules.json")

# 曜日名（日本語）
WEEKDAYS_JP = ["月曜日", "火曜日", "水曜日", "木曜日", "金曜日", "土曜日", "日曜日"]
WEEKDAYS_SHORT = ["月", "火", "水", "木", "金", "土", "日"]

# x時間後の選択肢
HOURS_LATER_OPTIONS = [1, 3, 6, 9, 12]


# =============================================================================
# MACアドレス取得関数
# =============================================================================
def get_mac_addresses():
    """
    PCのMACアドレスを取得
    Returns: list of dict {"name": str, "mac": str}
    """
    mac_list = []
    try:
        # Windowsの場合 getmac コマンドを使用
        result = subprocess.run(
            ["getmac", "/v", "/fo", "csv"],
            capture_output=True, text=True, check=True,
            creationflags=subprocess.CREATE_NO_WINDOW
        )
        lines = result.stdout.strip().split('\n')
        if len(lines) > 1:
            for line in lines[1:]:
                # CSVパース
                parts = line.replace('"', '').split(',')
                if len(parts) >= 2:
                    name = parts[0].strip()
                    mac = parts[1].strip()
                    if mac and mac != "N/A" and "Media disconnected" not in mac:
                        mac_list.append({"name": name, "mac": mac})
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
# スケジュール管理クラス
# =============================================================================
class ScheduleManager:
    """
    シャットダウンスケジュールを管理するクラス
    優先順位: 一回限り > 毎週 > 毎日
    """
    
    def __init__(self, config_path=CONFIG_FILE):
        self.config_path = config_path
        
        # デフォルト設定
        self.daily_schedule = {
            "enabled": False,
            "hour": 23,
            "minute": 0
        }
        
        # 毎週スケジュール: 各曜日ごとに個別設定
        self.weekly_schedules = []
        
        # 一回限りスケジュール
        self.onetime_schedules = []
        
        # デバッグモード
        self.debug_mode = True
        
        # 設定を読み込み
        self.load()
    
    def load(self):
        """設定ファイルから読み込み"""
        if os.path.exists(self.config_path):
            try:
                with open(self.config_path, 'r', encoding='utf-8') as f:
                    data = json.load(f)
                    self.daily_schedule = data.get("daily", self.daily_schedule)
                    
                    # v1.0.0からの移行対応
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
            except Exception as e:
                print(f"設定の読み込みに失敗: {e}")
    
    def save(self):
        """設定ファイルに保存"""
        data = {
            "daily": self.daily_schedule,
            "weekly_schedules": self.weekly_schedules,
            "onetime": self.onetime_schedules,
            "debug_mode": self.debug_mode
        }
        try:
            with open(self.config_path, 'w', encoding='utf-8') as f:
                json.dump(data, f, ensure_ascii=False, indent=2)
        except Exception as e:
            print(f"設定の保存に失敗: {e}")
    
    # --- 毎週スケジュール操作 ---
    def add_weekly(self, weekday, hour, minute):
        """毎週スケジュールを追加"""
        schedule = {
            "id": str(uuid.uuid4()),
            "weekday": weekday,
            "hour": hour,
            "minute": minute
        }
        self.weekly_schedules.append(schedule)
        self.save()
        return schedule["id"]
    
    def remove_weekly(self, schedule_id):
        """毎週スケジュールを削除"""
        self.weekly_schedules = [
            s for s in self.weekly_schedules if s["id"] != schedule_id
        ]
        self.save()
    
    # --- 一回限りスケジュール操作 ---
    def add_onetime(self, dt_str):
        """一回限りスケジュールを追加"""
        schedule = {
            "id": str(uuid.uuid4()),
            "datetime": dt_str,
            "executed": False
        }
        self.onetime_schedules.append(schedule)
        self.save()
        return schedule["id"]
    
    def add_onetime_hours_later(self, hours):
        """x時間後のスケジュールを追加"""
        target_time = datetime.now() + timedelta(hours=hours)
        dt_str = target_time.strftime("%Y-%m-%d %H:%M")
        return self.add_onetime(dt_str)
    
    def remove_onetime(self, schedule_id):
        """一回限りスケジュールを削除"""
        self.onetime_schedules = [
            s for s in self.onetime_schedules if s["id"] != schedule_id
        ]
        self.save()
    
    def clear_executed_onetime(self):
        """実行済みの一回限りスケジュールを削除"""
        self.onetime_schedules = [
            s for s in self.onetime_schedules if not s.get("executed", False)
        ]
        self.save()
    
    def get_next_shutdown_info(self):
        """次のシャットダウン予定を取得"""
        now = datetime.now()
        candidates = []
        
        # 一回限りスケジュール（優先度1）
        for s in self.onetime_schedules:
            if s.get("executed", False):
                continue
            try:
                dt = datetime.strptime(s["datetime"], "%Y-%m-%d %H:%M")
                if dt > now:
                    candidates.append((dt, "onetime", s["id"]))
            except ValueError:
                pass
        
        # 毎週スケジュール（優先度2）
        for s in self.weekly_schedules:
            target_weekday = s["weekday"]
            target_time = now.replace(
                hour=s["hour"],
                minute=s["minute"],
                second=0, microsecond=0
            )
            days_ahead = target_weekday - now.weekday()
            if days_ahead < 0 or (days_ahead == 0 and target_time <= now):
                days_ahead += 7
            next_weekly = target_time + timedelta(days=days_ahead)
            candidates.append((next_weekly, "weekly", s["id"]))
        
        # 毎日スケジュール（優先度3）
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
        """現在時刻でシャットダウンすべきか確認し、必要なら実行"""
        now = datetime.now()
        current_weekday = now.weekday()
        
        # 一回限りスケジュールをチェック（最優先）
        for s in self.onetime_schedules:
            if s.get("executed", False):
                continue
            try:
                scheduled_dt = datetime.strptime(s["datetime"], "%Y-%m-%d %H:%M")
                if abs((now - scheduled_dt).total_seconds()) < 60:
                    s["executed"] = True
                    self.save()
                    trigger_type = f"一回限り ({s['datetime']})"
                    self._execute_shutdown(trigger_type, log_callback)
                    return True
            except ValueError:
                pass
        
        # 毎週スケジュールをチェック（優先度2）
        for s in self.weekly_schedules:
            if (current_weekday == s["weekday"] and
                now.hour == s["hour"] and
                now.minute == s["minute"]):
                trigger_type = f"毎週 ({WEEKDAYS_JP[s['weekday']]} " \
                              f"{s['hour']:02d}:{s['minute']:02d})"
                self._execute_shutdown(trigger_type, log_callback)
                return True
        
        # 毎日スケジュールをチェック（優先度3）
        if self.daily_schedule["enabled"]:
            if (now.hour == self.daily_schedule["hour"] and
                now.minute == self.daily_schedule["minute"]):
                trigger_type = f"毎日 ({self.daily_schedule['hour']:02d}:" \
                              f"{self.daily_schedule['minute']:02d})"
                self._execute_shutdown(trigger_type, log_callback)
                return True
        
        return False
    
    def _execute_shutdown(self, trigger_type, log_callback=None):
        """シャットダウンを実行"""
        msg = f"[{datetime.now().strftime('%Y-%m-%d %H:%M:%S')}] " \
              f"シャットダウン実行: {trigger_type}"
        
        if log_callback:
            log_callback(msg)
        
        if self.debug_mode:
            if log_callback:
                log_callback("[デバッグモード] 実際のシャットダウンはスキップされました")
            return
        
        try:
            subprocess.run(["shutdown", "/s", "/t", "60", "/c", 
                          f"SmartPowerManager: {trigger_type}"], check=True)
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
        self.geometry("700x850")
        self.minsize(600, 750)
        
        # スケジュールマネージャー
        self.schedule_manager = ScheduleManager()
        
        # 監視スレッド制御
        self.monitor_running = False
        self.monitor_thread = None
        
        # UI構築
        self._setup_widgets()
        
        # 初期表示を更新
        self._update_schedule_display()
        
        # 監視スレッド開始
        self._start_monitor()
        
        # ウィンドウ閉じる時の処理
        self.protocol("WM_DELETE_WINDOW", self._on_close)
    
    def _setup_widgets(self):
        """ウィジェットをセットアップ"""
        main_frame = ttk.Frame(self, padding="10")
        main_frame.pack(fill=tk.BOTH, expand=True)
        
        # タブ作成
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
        
        # ログ表示
        log_frame = ttk.LabelFrame(main_frame, text="ログ", padding="5")
        log_frame.pack(fill=tk.BOTH, expand=True, pady=(10, 0))
        
        self.log_text = tk.Text(log_frame, height=5, state="disabled", wrap="word")
        scrollbar = ttk.Scrollbar(log_frame, orient=tk.VERTICAL, 
                                 command=self.log_text.yview)
        self.log_text.config(yscrollcommand=scrollbar.set)
        self.log_text.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        scrollbar.pack(side=tk.RIGHT, fill=tk.Y)
        
        # ステータスバー
        self.status_var = tk.StringVar(value="準備完了")
        status_bar = ttk.Label(main_frame, textvariable=self.status_var, 
                              relief=tk.SUNKEN, anchor=tk.W)
        status_bar.pack(fill=tk.X, pady=(5, 0))
    
    def _setup_shutdown_tab(self):
        """シャットダウンタブの設定"""
        # スクロール可能なフレーム
        canvas = tk.Canvas(self.shutdown_tab, highlightthickness=0)
        scrollbar = ttk.Scrollbar(self.shutdown_tab, orient="vertical", 
                                 command=canvas.yview)
        scrollable_frame = ttk.Frame(canvas)
        
        scrollable_frame.bind(
            "<Configure>",
            lambda e: canvas.configure(scrollregion=canvas.bbox("all"))
        )
        
        canvas.create_window((0, 0), window=scrollable_frame, anchor="nw")
        canvas.configure(yscrollcommand=scrollbar.set)
        
        canvas.pack(side="left", fill="both", expand=True)
        scrollbar.pack(side="right", fill="y")
        
        # マウスホイールでスクロール
        def _on_mousewheel(event):
            canvas.yview_scroll(int(-1*(event.delta/120)), "units")
        canvas.bind_all("<MouseWheel>", _on_mousewheel)
        
        # === x時間後にシャットダウン（クイック設定） ===
        quick_frame = ttk.LabelFrame(scrollable_frame, 
                                    text="クイック設定：x時間後にシャットダウン", 
                                    padding="10")
        quick_frame.pack(fill=tk.X, pady=5, padx=5)
        
        quick_btn_frame = ttk.Frame(quick_frame)
        quick_btn_frame.pack(fill=tk.X)
        
        for hours in HOURS_LATER_OPTIONS:
            btn = ttk.Button(
                quick_btn_frame, 
                text=f"{hours}時間後",
                command=lambda h=hours: self._add_hours_later(h)
            )
            btn.pack(side=tk.LEFT, padx=5, pady=5)
        
        # === 毎日スケジュール ===
        daily_frame = ttk.LabelFrame(scrollable_frame, text="毎日スケジュール", 
                                    padding="10")
        daily_frame.pack(fill=tk.X, pady=5, padx=5)
        
        daily_row = ttk.Frame(daily_frame)
        daily_row.pack(fill=tk.X)
        
        self.daily_enabled_var = tk.BooleanVar(
            value=self.schedule_manager.daily_schedule["enabled"]
        )
        ttk.Checkbutton(daily_row, text="有効", 
                       variable=self.daily_enabled_var,
                       command=self._on_daily_changed).pack(side=tk.LEFT)
        
        ttk.Label(daily_row, text="時刻:").pack(side=tk.LEFT, padx=(20, 5))
        
        self.daily_hour_var = tk.StringVar(
            value=f"{self.schedule_manager.daily_schedule['hour']:02d}"
        )
        ttk.Spinbox(daily_row, from_=0, to=23, width=3,
                   textvariable=self.daily_hour_var,
                   command=self._on_daily_changed).pack(side=tk.LEFT)
        
        ttk.Label(daily_row, text=":").pack(side=tk.LEFT)
        
        self.daily_minute_var = tk.StringVar(
            value=f"{self.schedule_manager.daily_schedule['minute']:02d}"
        )
        ttk.Spinbox(daily_row, from_=0, to=59, width=3,
                   textvariable=self.daily_minute_var,
                   command=self._on_daily_changed).pack(side=tk.LEFT)
        
        # === 毎週スケジュール ===
        weekly_frame = ttk.LabelFrame(scrollable_frame, 
                                     text="毎週スケジュール（曜日別・複数登録可）", 
                                     padding="10")
        weekly_frame.pack(fill=tk.BOTH, expand=True, pady=5, padx=5)
        
        weekly_add_frame = ttk.Frame(weekly_frame)
        weekly_add_frame.pack(fill=tk.X, pady=(0, 10))
        
        ttk.Label(weekly_add_frame, text="曜日:").pack(side=tk.LEFT)
        
        self.weekly_add_day_var = tk.StringVar(value=WEEKDAYS_JP[0])
        weekly_day_combo = ttk.Combobox(weekly_add_frame, 
                                        textvariable=self.weekly_add_day_var,
                                        values=WEEKDAYS_JP, width=8, state="readonly")
        weekly_day_combo.pack(side=tk.LEFT, padx=5)
        
        ttk.Label(weekly_add_frame, text="時刻:").pack(side=tk.LEFT, padx=(10, 5))
        
        self.weekly_add_hour_var = tk.StringVar(value="23")
        ttk.Spinbox(weekly_add_frame, from_=0, to=23, width=3,
                   textvariable=self.weekly_add_hour_var).pack(side=tk.LEFT)
        
        ttk.Label(weekly_add_frame, text=":").pack(side=tk.LEFT)
        
        self.weekly_add_minute_var = tk.StringVar(value="00")
        ttk.Spinbox(weekly_add_frame, from_=0, to=59, width=3,
                   textvariable=self.weekly_add_minute_var).pack(side=tk.LEFT)
        
        ttk.Button(weekly_add_frame, text="追加", 
                  command=self._add_weekly).pack(side=tk.LEFT, padx=(15, 0))
        
        # 毎週スケジュール一覧
        weekly_list_frame = ttk.Frame(weekly_frame)
        weekly_list_frame.pack(fill=tk.BOTH, expand=True)
        
        weekly_columns = ("weekday", "time")
        self.weekly_tree = ttk.Treeview(weekly_list_frame, columns=weekly_columns, 
                                        show="headings", height=3)
        self.weekly_tree.heading("weekday", text="曜日")
        self.weekly_tree.heading("time", text="時刻")
        self.weekly_tree.column("weekday", width=100)
        self.weekly_tree.column("time", width=80)
        
        weekly_tree_scroll = ttk.Scrollbar(weekly_list_frame, orient=tk.VERTICAL,
                                          command=self.weekly_tree.yview)
        self.weekly_tree.configure(yscrollcommand=weekly_tree_scroll.set)
        
        self.weekly_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        weekly_tree_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        
        weekly_btn_frame = ttk.Frame(weekly_frame)
        weekly_btn_frame.pack(fill=tk.X, pady=(5, 0))
        
        ttk.Button(weekly_btn_frame, text="選択を削除", 
                  command=self._remove_selected_weekly).pack(side=tk.LEFT)
        
        # === 一回限りスケジュール ===
        onetime_frame = ttk.LabelFrame(scrollable_frame, 
                                       text="一回限りスケジュール（最優先）", 
                                       padding="10")
        onetime_frame.pack(fill=tk.BOTH, expand=True, pady=5, padx=5)
        
        add_frame = ttk.Frame(onetime_frame)
        add_frame.pack(fill=tk.X, pady=(0, 10))
        
        ttk.Label(add_frame, text="日付:").pack(side=tk.LEFT)
        
        self.onetime_year_var = tk.StringVar(value=str(datetime.now().year))
        ttk.Spinbox(add_frame, from_=2024, to=2100, width=5,
                   textvariable=self.onetime_year_var).pack(side=tk.LEFT, padx=2)
        ttk.Label(add_frame, text="/").pack(side=tk.LEFT)
        
        self.onetime_month_var = tk.StringVar(value=f"{datetime.now().month:02d}")
        ttk.Spinbox(add_frame, from_=1, to=12, width=3,
                   textvariable=self.onetime_month_var).pack(side=tk.LEFT, padx=2)
        ttk.Label(add_frame, text="/").pack(side=tk.LEFT)
        
        self.onetime_day_var = tk.StringVar(value=f"{datetime.now().day:02d}")
        ttk.Spinbox(add_frame, from_=1, to=31, width=3,
                   textvariable=self.onetime_day_var).pack(side=tk.LEFT, padx=2)
        
        ttk.Label(add_frame, text="時刻:").pack(side=tk.LEFT, padx=(15, 5))
        
        self.onetime_hour_var = tk.StringVar(value="23")
        ttk.Spinbox(add_frame, from_=0, to=23, width=3,
                   textvariable=self.onetime_hour_var).pack(side=tk.LEFT)
        ttk.Label(add_frame, text=":").pack(side=tk.LEFT)
        
        self.onetime_minute_var = tk.StringVar(value="00")
        ttk.Spinbox(add_frame, from_=0, to=59, width=3,
                   textvariable=self.onetime_minute_var).pack(side=tk.LEFT)
        
        ttk.Button(add_frame, text="追加", 
                  command=self._add_onetime).pack(side=tk.LEFT, padx=(15, 0))
        
        # 一覧表示
        list_frame = ttk.Frame(onetime_frame)
        list_frame.pack(fill=tk.BOTH, expand=True)
        
        columns = ("datetime", "status")
        self.onetime_tree = ttk.Treeview(list_frame, columns=columns, 
                                         show="headings", height=3)
        self.onetime_tree.heading("datetime", text="日時")
        self.onetime_tree.heading("status", text="状態")
        self.onetime_tree.column("datetime", width=150)
        self.onetime_tree.column("status", width=80)
        
        tree_scroll = ttk.Scrollbar(list_frame, orient=tk.VERTICAL,
                                   command=self.onetime_tree.yview)
        self.onetime_tree.configure(yscrollcommand=tree_scroll.set)
        
        self.onetime_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        tree_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        
        btn_frame = ttk.Frame(onetime_frame)
        btn_frame.pack(fill=tk.X, pady=(5, 0))
        
        ttk.Button(btn_frame, text="選択を削除", 
                  command=self._remove_selected_onetime).pack(side=tk.LEFT)
        ttk.Button(btn_frame, text="実行済みを削除", 
                  command=self._clear_executed_onetime).pack(side=tk.LEFT, padx=5)
        
        # 次回シャットダウン表示
        next_frame = ttk.LabelFrame(scrollable_frame, text="次回シャットダウン", 
                                   padding="10")
        next_frame.pack(fill=tk.X, pady=5, padx=5)
        
        self.next_shutdown_var = tk.StringVar(value="スケジュールなし")
        ttk.Label(next_frame, textvariable=self.next_shutdown_var,
                 font=("", 11, "bold")).pack(anchor=tk.W)
    
    def _setup_autoboot_tab(self):
        """自動起動タブの設定（MACアドレス表示）"""
        info_frame = ttk.Frame(self.autoboot_tab)
        info_frame.pack(fill=tk.X, pady=10)
        
        ttk.Label(info_frame, 
                 text="🔧 自動起動機能",
                 font=("", 14, "bold")).pack(anchor=tk.W)
        
        ttk.Label(info_frame, 
                 text="Raspberry Pi Pico W を使用してPCの自動起動を行います。\n"
                      "以下のMACアドレスをPico W側で設定してください。",
                 justify=tk.LEFT).pack(anchor=tk.W, pady=(5, 0))
        
        # MACアドレス表示
        mac_frame = ttk.LabelFrame(self.autoboot_tab, 
                                   text="このPCのMACアドレス", padding="10")
        mac_frame.pack(fill=tk.X, pady=10)
        
        mac_list = get_mac_addresses()
        
        if mac_list:
            for i, mac_info in enumerate(mac_list):
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
            ttk.Label(mac_frame, 
                     text="MACアドレスを取得できませんでした",
                     foreground="red").pack(anchor=tk.W)
        
        ttk.Button(mac_frame, text="再取得", 
                  command=self._refresh_mac_addresses).pack(anchor=tk.W, pady=(10, 0))
        
        # Pico W設定説明
        pico_frame = ttk.LabelFrame(self.autoboot_tab, 
                                    text="Raspberry Pi Pico W 設定", padding="10")
        pico_frame.pack(fill=tk.X, pady=10)
        
        ttk.Label(pico_frame, 
                 text="【設定手順】\n"
                      "1. 上記のMACアドレスをメモまたはコピー\n"
                      "2. Pico Wのコードに対象MACアドレスを設定\n"
                      "3. 起動スケジュールをPico W側で設定\n\n"
                      "※ この機能は将来のアップデートで\n"
                      "   より詳細な設定が可能になります",
                 justify=tk.LEFT).pack(anchor=tk.W)
        
        self.mac_frame = mac_frame
    
    def _refresh_mac_addresses(self):
        """MACアドレスを再取得"""
        for widget in self.mac_frame.winfo_children():
            widget.destroy()
        
        mac_list = get_mac_addresses()
        
        if mac_list:
            for i, mac_info in enumerate(mac_list):
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
            ttk.Label(self.mac_frame, 
                     text="MACアドレスを取得できませんでした",
                     foreground="red").pack(anchor=tk.W)
        
        ttk.Button(self.mac_frame, text="再取得", 
                  command=self._refresh_mac_addresses).pack(anchor=tk.W, pady=(10, 0))
        
        self._log("MACアドレスを再取得しました")
    
    def _setup_update_tab(self):
        """アップデートタブの設定（プレースホルダー）"""
        placeholder_frame = ttk.Frame(self.update_tab)
        placeholder_frame.pack(expand=True)
        
        ttk.Label(placeholder_frame, 
                 text="📦 アップデート機能",
                 font=("", 14, "bold")).pack(pady=10)
        
        ttk.Label(placeholder_frame, 
                 text="この機能は将来のアップデートで実装予定です。\n"
                      "GitHubからの自動更新を実現します。",
                 justify=tk.CENTER).pack(pady=20)
        
        url_frame = ttk.LabelFrame(placeholder_frame, text="GitHub設定", padding="10")
        url_frame.pack(fill=tk.X, padx=20, pady=10)
        
        ttk.Label(url_frame, text="リポジトリURL:").pack(anchor=tk.W)
        self.github_url_var = tk.StringVar(value="")
        ttk.Entry(url_frame, textvariable=self.github_url_var, 
                 width=50, state="disabled").pack(fill=tk.X, pady=5)
        
        ttk.Label(url_frame, 
                 text="※ URLは後から設定されます",
                 foreground="gray").pack(anchor=tk.W)
    
    def _setup_settings_tab(self):
        """設定タブの設定"""
        settings_frame = ttk.Frame(self.settings_tab)
        settings_frame.pack(fill=tk.BOTH, expand=True)
        
        debug_frame = ttk.LabelFrame(settings_frame, text="動作モード", padding="10")
        debug_frame.pack(fill=tk.X, pady=5)
        
        self.debug_mode_var = tk.BooleanVar(value=self.schedule_manager.debug_mode)
        ttk.Checkbutton(debug_frame, 
                       text="デバッグモード（実際にシャットダウンしない）",
                       variable=self.debug_mode_var,
                       command=self._on_debug_mode_changed).pack(anchor=tk.W)
        
        ttk.Label(debug_frame, 
                 text="※ 初回使用時はデバッグモードを有効にして\n"
                      "   動作確認することをお勧めします。",
                 foreground="gray").pack(anchor=tk.W, pady=(5, 0))
        
        version_frame = ttk.LabelFrame(settings_frame, text="バージョン情報", 
                                       padding="10")
        version_frame.pack(fill=tk.X, pady=5)
        
        ttk.Label(version_frame, 
                 text=f"SmartPowerManager v{APP_VERSION}",
                 font=("", 10, "bold")).pack(anchor=tk.W)
        ttk.Label(version_frame, 
                 text="PCシャットダウンスケジュール管理アプリ").pack(anchor=tk.W)
    
    # =========================================================================
    # イベントハンドラ
    # =========================================================================
    def _add_hours_later(self, hours):
        """x時間後のシャットダウンを追加"""
        self.schedule_manager.add_onetime_hours_later(hours)
        self._update_schedule_display()
        target_time = datetime.now() + timedelta(hours=hours)
        self._log(f"{hours}時間後にシャットダウン予約: {target_time.strftime('%Y-%m-%d %H:%M')}")
    
    def _on_daily_changed(self):
        """毎日スケジュールの変更"""
        try:
            self.schedule_manager.daily_schedule["enabled"] = self.daily_enabled_var.get()
            self.schedule_manager.daily_schedule["hour"] = int(self.daily_hour_var.get())
            self.schedule_manager.daily_schedule["minute"] = int(self.daily_minute_var.get())
            self.schedule_manager.save()
            self._update_schedule_display()
            self._log("毎日スケジュールを更新しました")
        except ValueError:
            pass
    
    def _add_weekly(self):
        """毎週スケジュールを追加"""
        try:
            weekday_name = self.weekly_add_day_var.get()
            if weekday_name not in WEEKDAYS_JP:
                return
            weekday = WEEKDAYS_JP.index(weekday_name)
            hour = int(self.weekly_add_hour_var.get())
            minute = int(self.weekly_add_minute_var.get())
            
            self.schedule_manager.add_weekly(weekday, hour, minute)
            self._update_schedule_display()
            self._log(f"毎週スケジュールを追加: {weekday_name} {hour:02d}:{minute:02d}")
        except ValueError as e:
            messagebox.showerror("エラー", f"無効な時刻です: {e}")
    
    def _remove_selected_weekly(self):
        """選択された毎週スケジュールを削除"""
        selected = self.weekly_tree.selection()
        if not selected:
            messagebox.showinfo("情報", "削除するスケジュールを選択してください")
            return
        
        for item in selected:
            schedule_id = self.weekly_tree.item(item)["tags"][0]
            self.schedule_manager.remove_weekly(schedule_id)
        
        self._update_schedule_display()
        self._log("選択したスケジュールを削除しました")
    
    def _add_onetime(self):
        """一回限りスケジュールを追加"""
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
            self._log(f"一回限りスケジュールを追加: {dt_str}")
            
        except ValueError as e:
            messagebox.showerror("エラー", f"無効な日時です: {e}")
    
    def _remove_selected_onetime(self):
        """選択された一回限りスケジュールを削除"""
        selected = self.onetime_tree.selection()
        if not selected:
            messagebox.showinfo("情報", "削除するスケジュールを選択してください")
            return
        
        for item in selected:
            schedule_id = self.onetime_tree.item(item)["tags"][0]
            self.schedule_manager.remove_onetime(schedule_id)
        
        self._update_schedule_display()
        self._log("選択したスケジュールを削除しました")
    
    def _clear_executed_onetime(self):
        """実行済みの一回限りスケジュールを削除"""
        self.schedule_manager.clear_executed_onetime()
        self._update_schedule_display()
        self._log("実行済みスケジュールを削除しました")
    
    def _on_debug_mode_changed(self):
        """デバッグモードの変更"""
        self.schedule_manager.debug_mode = self.debug_mode_var.get()
        self.schedule_manager.save()
        mode_str = "有効" if self.schedule_manager.debug_mode else "無効"
        self._log(f"デバッグモードを{mode_str}にしました")
    
    # =========================================================================
    # 表示更新
    # =========================================================================
    def _update_schedule_display(self):
        """スケジュール表示を更新"""
        # 毎週リストを更新
        for item in self.weekly_tree.get_children():
            self.weekly_tree.delete(item)
        
        for s in self.schedule_manager.weekly_schedules:
            weekday_name = WEEKDAYS_JP[s["weekday"]]
            time_str = f"{s['hour']:02d}:{s['minute']:02d}"
            self.weekly_tree.insert("", tk.END, 
                                   values=(weekday_name, time_str),
                                   tags=(s["id"],))
        
        # 一回限りリストを更新
        for item in self.onetime_tree.get_children():
            self.onetime_tree.delete(item)
        
        for s in self.schedule_manager.onetime_schedules:
            status = "実行済み" if s.get("executed", False) else "待機中"
            self.onetime_tree.insert("", tk.END, 
                                    values=(s["datetime"], status),
                                    tags=(s["id"],))
        
        # 次回シャットダウンを更新
        next_dt, next_type, _ = self.schedule_manager.get_next_shutdown_info()
        if next_dt:
            type_names = {"onetime": "一回限り", "weekly": "毎週", "daily": "毎日"}
            type_name = type_names.get(next_type, next_type)
            self.next_shutdown_var.set(
                f"{next_dt.strftime('%Y-%m-%d %H:%M')} ({type_name})"
            )
        else:
            self.next_shutdown_var.set("スケジュールなし")
    
    def _log(self, message):
        """ログに追加"""
        timestamp = datetime.now().strftime("%H:%M:%S")
        self.log_text.config(state="normal")
        self.log_text.insert(tk.END, f"[{timestamp}] {message}\n")
        self.log_text.see(tk.END)
        self.log_text.config(state="disabled")
    
    # =========================================================================
    # 監視スレッド
    # =========================================================================
    def _start_monitor(self):
        """監視スレッドを開始"""
        self.monitor_running = True
        self.monitor_thread = threading.Thread(target=self._monitor_loop, daemon=True)
        self.monitor_thread.start()
        self._log("スケジュール監視を開始しました")
    
    def _monitor_loop(self):
        """監視ループ（バックグラウンドスレッド）"""
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
                
                self.after(0, self._update_status)
            
            time.sleep(5)
    
    def _update_status(self):
        """ステータスバーを更新"""
        next_dt, next_type, _ = self.schedule_manager.get_next_shutdown_info()
        if next_dt:
            remaining = next_dt - datetime.now()
            hours, remainder = divmod(int(remaining.total_seconds()), 3600)
            minutes, _ = divmod(remainder, 60)
            self.status_var.set(f"次回シャットダウンまで: {hours}時間{minutes}分")
        else:
            self.status_var.set("スケジュールなし")
    
    def _on_close(self):
        """ウィンドウを閉じる時の処理"""
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
