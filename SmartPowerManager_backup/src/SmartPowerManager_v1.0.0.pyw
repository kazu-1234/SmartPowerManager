# version: 1.0.0
# -*- coding: utf-8 -*-
"""
SmartPowerManager v1.0.0
PCのシャットダウンスケジュール管理アプリケーション

機能:
- 毎日/毎週/一回限りのシャットダウンスケジュール
- 優先順位: 一回限り > 毎週 > 毎日
- 将来実装: Pico W自動起動、GitHubアップデート
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
APP_VERSION = "1.0.0"
APP_TITLE = f"SmartPowerManager v{APP_VERSION}"
CONFIG_FILE = os.path.join(os.path.dirname(os.path.abspath(__file__)), "schedules.json")

# 曜日名（日本語）
WEEKDAYS_JP = ["月曜日", "火曜日", "水曜日", "木曜日", "金曜日", "土曜日", "日曜日"]
WEEKDAYS_SHORT = ["月", "火", "水", "木", "金", "土", "日"]


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
        self.weekly_schedule = {
            "enabled": False,
            "weekday": 0,  # 0=月曜日, 6=日曜日
            "hour": 23,
            "minute": 0
        }
        self.onetime_schedules = []  # [{"id": uuid, "datetime": "YYYY-MM-DD HH:MM", "executed": False}]
        
        # デバッグモード（Trueの場合、実際にシャットダウンせずログのみ）
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
                    self.weekly_schedule = data.get("weekly", self.weekly_schedule)
                    self.onetime_schedules = data.get("onetime", [])
                    self.debug_mode = data.get("debug_mode", True)
            except Exception as e:
                print(f"設定の読み込みに失敗: {e}")
    
    def save(self):
        """設定ファイルに保存"""
        data = {
            "daily": self.daily_schedule,
            "weekly": self.weekly_schedule,
            "onetime": self.onetime_schedules,
            "debug_mode": self.debug_mode
        }
        try:
            with open(self.config_path, 'w', encoding='utf-8') as f:
                json.dump(data, f, ensure_ascii=False, indent=2)
        except Exception as e:
            print(f"設定の保存に失敗: {e}")
    
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
        """
        次のシャットダウン予定を取得
        Returns: (datetime, type_str) or (None, None)
        """
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
        if self.weekly_schedule["enabled"]:
            target_weekday = self.weekly_schedule["weekday"]
            target_time = now.replace(
                hour=self.weekly_schedule["hour"],
                minute=self.weekly_schedule["minute"],
                second=0, microsecond=0
            )
            days_ahead = target_weekday - now.weekday()
            if days_ahead < 0 or (days_ahead == 0 and target_time <= now):
                days_ahead += 7
            next_weekly = target_time + timedelta(days=days_ahead)
            candidates.append((next_weekly, "weekly", None))
        
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
        
        # 日時でソートして最も早いものを返す
        candidates.sort(key=lambda x: x[0])
        return candidates[0]
    
    def check_and_execute(self, log_callback=None):
        """
        現在時刻でシャットダウンすべきか確認し、必要なら実行
        優先順位: 一回限り > 毎週 > 毎日
        """
        now = datetime.now()
        current_time_str = now.strftime("%H:%M")
        current_weekday = now.weekday()
        current_date_str = now.strftime("%Y-%m-%d")
        
        shutdown_triggered = False
        trigger_type = None
        
        # 一回限りスケジュールをチェック（最優先）
        for s in self.onetime_schedules:
            if s.get("executed", False):
                continue
            try:
                scheduled_dt = datetime.strptime(s["datetime"], "%Y-%m-%d %H:%M")
                # 1分以内の誤差を許容
                if abs((now - scheduled_dt).total_seconds()) < 60:
                    s["executed"] = True
                    self.save()
                    shutdown_triggered = True
                    trigger_type = f"一回限り ({s['datetime']})"
                    break
            except ValueError:
                pass
        
        # 一回限りがあれば他はスキップ
        if shutdown_triggered:
            self._execute_shutdown(trigger_type, log_callback)
            return True
        
        # 毎週スケジュールをチェック（優先度2）
        if self.weekly_schedule["enabled"]:
            if (current_weekday == self.weekly_schedule["weekday"] and
                now.hour == self.weekly_schedule["hour"] and
                now.minute == self.weekly_schedule["minute"]):
                trigger_type = f"毎週 ({WEEKDAYS_JP[self.weekly_schedule['weekday']]} " \
                              f"{self.weekly_schedule['hour']:02d}:{self.weekly_schedule['minute']:02d})"
                self._execute_shutdown(trigger_type, log_callback)
                return True
        
        # 毎日スケジュールをチェック（優先度3）
        if self.daily_schedule["enabled"]:
            if (now.hour == self.daily_schedule["hour"] and
                now.minute == self.daily_schedule["minute"]):
                trigger_type = f"毎日 ({self.daily_schedule['hour']:02d}:{self.daily_schedule['minute']:02d})"
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
        
        # Windowsシャットダウンコマンド
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
        self.geometry("650x700")
        self.minsize(550, 600)
        
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
        # メインフレーム
        main_frame = ttk.Frame(self, padding="10")
        main_frame.pack(fill=tk.BOTH, expand=True)
        
        # タブ作成
        notebook = ttk.Notebook(main_frame)
        notebook.pack(fill=tk.BOTH, expand=True)
        
        # シャットダウンタブ
        self.shutdown_tab = ttk.Frame(notebook, padding="10")
        notebook.add(self.shutdown_tab, text="シャットダウン")
        self._setup_shutdown_tab()
        
        # 自動起動タブ（プレースホルダー）
        self.autoboot_tab = ttk.Frame(notebook, padding="10")
        notebook.add(self.autoboot_tab, text="自動起動")
        self._setup_autoboot_tab()
        
        # アップデートタブ（プレースホルダー）
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
        
        self.log_text = tk.Text(log_frame, height=8, state="disabled", wrap="word")
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
        # === 毎日スケジュール ===
        daily_frame = ttk.LabelFrame(self.shutdown_tab, text="毎日スケジュール", 
                                    padding="10")
        daily_frame.pack(fill=tk.X, pady=5)
        
        self.daily_enabled_var = tk.BooleanVar(
            value=self.schedule_manager.daily_schedule["enabled"]
        )
        ttk.Checkbutton(daily_frame, text="有効", 
                       variable=self.daily_enabled_var,
                       command=self._on_daily_changed).pack(side=tk.LEFT)
        
        ttk.Label(daily_frame, text="時刻:").pack(side=tk.LEFT, padx=(20, 5))
        
        self.daily_hour_var = tk.StringVar(
            value=f"{self.schedule_manager.daily_schedule['hour']:02d}"
        )
        daily_hour_spin = ttk.Spinbox(daily_frame, from_=0, to=23, width=3,
                                      textvariable=self.daily_hour_var,
                                      command=self._on_daily_changed)
        daily_hour_spin.pack(side=tk.LEFT)
        
        ttk.Label(daily_frame, text=":").pack(side=tk.LEFT)
        
        self.daily_minute_var = tk.StringVar(
            value=f"{self.schedule_manager.daily_schedule['minute']:02d}"
        )
        daily_minute_spin = ttk.Spinbox(daily_frame, from_=0, to=59, width=3,
                                        textvariable=self.daily_minute_var,
                                        command=self._on_daily_changed)
        daily_minute_spin.pack(side=tk.LEFT)
        
        # === 毎週スケジュール ===
        weekly_frame = ttk.LabelFrame(self.shutdown_tab, text="毎週スケジュール", 
                                     padding="10")
        weekly_frame.pack(fill=tk.X, pady=5)
        
        self.weekly_enabled_var = tk.BooleanVar(
            value=self.schedule_manager.weekly_schedule["enabled"]
        )
        ttk.Checkbutton(weekly_frame, text="有効", 
                       variable=self.weekly_enabled_var,
                       command=self._on_weekly_changed).pack(side=tk.LEFT)
        
        ttk.Label(weekly_frame, text="曜日:").pack(side=tk.LEFT, padx=(20, 5))
        
        self.weekly_day_var = tk.StringVar(
            value=WEEKDAYS_JP[self.schedule_manager.weekly_schedule["weekday"]]
        )
        weekly_day_combo = ttk.Combobox(weekly_frame, textvariable=self.weekly_day_var,
                                        values=WEEKDAYS_JP, width=8, state="readonly")
        weekly_day_combo.pack(side=tk.LEFT)
        weekly_day_combo.bind("<<ComboboxSelected>>", lambda e: self._on_weekly_changed())
        
        ttk.Label(weekly_frame, text="時刻:").pack(side=tk.LEFT, padx=(15, 5))
        
        self.weekly_hour_var = tk.StringVar(
            value=f"{self.schedule_manager.weekly_schedule['hour']:02d}"
        )
        weekly_hour_spin = ttk.Spinbox(weekly_frame, from_=0, to=23, width=3,
                                       textvariable=self.weekly_hour_var,
                                       command=self._on_weekly_changed)
        weekly_hour_spin.pack(side=tk.LEFT)
        
        ttk.Label(weekly_frame, text=":").pack(side=tk.LEFT)
        
        self.weekly_minute_var = tk.StringVar(
            value=f"{self.schedule_manager.weekly_schedule['minute']:02d}"
        )
        weekly_minute_spin = ttk.Spinbox(weekly_frame, from_=0, to=59, width=3,
                                         textvariable=self.weekly_minute_var,
                                         command=self._on_weekly_changed)
        weekly_minute_spin.pack(side=tk.LEFT)
        
        # === 一回限りスケジュール ===
        onetime_frame = ttk.LabelFrame(self.shutdown_tab, 
                                       text="一回限りスケジュール（最優先）", 
                                       padding="10")
        onetime_frame.pack(fill=tk.BOTH, expand=True, pady=5)
        
        # 追加用入力フレーム
        add_frame = ttk.Frame(onetime_frame)
        add_frame.pack(fill=tk.X, pady=(0, 10))
        
        ttk.Label(add_frame, text="日付:").pack(side=tk.LEFT)
        
        # 日付入力
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
                                         show="headings", height=5)
        self.onetime_tree.heading("datetime", text="日時")
        self.onetime_tree.heading("status", text="状態")
        self.onetime_tree.column("datetime", width=150)
        self.onetime_tree.column("status", width=80)
        
        tree_scroll = ttk.Scrollbar(list_frame, orient=tk.VERTICAL,
                                   command=self.onetime_tree.yview)
        self.onetime_tree.configure(yscrollcommand=tree_scroll.set)
        
        self.onetime_tree.pack(side=tk.LEFT, fill=tk.BOTH, expand=True)
        tree_scroll.pack(side=tk.RIGHT, fill=tk.Y)
        
        # 削除ボタン
        btn_frame = ttk.Frame(onetime_frame)
        btn_frame.pack(fill=tk.X, pady=(5, 0))
        
        ttk.Button(btn_frame, text="選択を削除", 
                  command=self._remove_selected_onetime).pack(side=tk.LEFT)
        ttk.Button(btn_frame, text="実行済みを削除", 
                  command=self._clear_executed_onetime).pack(side=tk.LEFT, padx=5)
        
        # 次回シャットダウン表示
        next_frame = ttk.LabelFrame(self.shutdown_tab, text="次回シャットダウン", 
                                   padding="10")
        next_frame.pack(fill=tk.X, pady=5)
        
        self.next_shutdown_var = tk.StringVar(value="スケジュールなし")
        ttk.Label(next_frame, textvariable=self.next_shutdown_var,
                 font=("", 11, "bold")).pack(anchor=tk.W)
    
    def _setup_autoboot_tab(self):
        """自動起動タブの設定（プレースホルダー）"""
        placeholder_frame = ttk.Frame(self.autoboot_tab)
        placeholder_frame.pack(expand=True)
        
        ttk.Label(placeholder_frame, 
                 text="🔧 自動起動機能",
                 font=("", 14, "bold")).pack(pady=10)
        
        ttk.Label(placeholder_frame, 
                 text="この機能は将来のアップデートで実装予定です。\n"
                      "Raspberry Pi Pico W を使用して\n"
                      "PCの自動起動を実現します。",
                 justify=tk.CENTER).pack(pady=20)
        
        ttk.Label(placeholder_frame, 
                 text="実装予定機能:\n"
                      "• Wake-on-LAN対応\n"
                      "• Pico Wとの連携設定\n"
                      "• 起動スケジュール管理",
                 justify=tk.LEFT).pack(pady=10)
    
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
        
        # GitHub URL設定（将来用）
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
        
        # デバッグモード
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
        
        # バージョン情報
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
    
    def _on_weekly_changed(self):
        """毎週スケジュールの変更"""
        try:
            self.schedule_manager.weekly_schedule["enabled"] = self.weekly_enabled_var.get()
            weekday_name = self.weekly_day_var.get()
            if weekday_name in WEEKDAYS_JP:
                self.schedule_manager.weekly_schedule["weekday"] = WEEKDAYS_JP.index(weekday_name)
            self.schedule_manager.weekly_schedule["hour"] = int(self.weekly_hour_var.get())
            self.schedule_manager.weekly_schedule["minute"] = int(self.weekly_minute_var.get())
            self.schedule_manager.save()
            self._update_schedule_display()
            self._log("毎週スケジュールを更新しました")
        except ValueError:
            pass
    
    def _add_onetime(self):
        """一回限りスケジュールを追加"""
        try:
            year = int(self.onetime_year_var.get())
            month = int(self.onetime_month_var.get())
            day = int(self.onetime_day_var.get())
            hour = int(self.onetime_hour_var.get())
            minute = int(self.onetime_minute_var.get())
            
            # 日付の妥当性チェック
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
            
            # 1分ごとにチェック（同じ分で複数回チェックしない）
            if current_minute != last_check_minute:
                last_check_minute = current_minute
                
                # スレッドセーフにログ出力
                def log_callback(msg):
                    self.after(0, lambda m=msg: self._log(m))
                
                # シャットダウンチェック
                triggered = self.schedule_manager.check_and_execute(log_callback)
                
                if triggered:
                    # 表示を更新
                    self.after(0, self._update_schedule_display)
                
                # ステータス更新
                self.after(0, self._update_status)
            
            time.sleep(5)  # 5秒ごとにチェック
    
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
