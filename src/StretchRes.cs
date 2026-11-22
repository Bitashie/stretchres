// StretchRes - change automatiquement la resolution de l'ecran quand un jeu choisi se lance
// (resolution etiree facon CS / Fortnite) puis remet la resolution native quand il se ferme.
// Compile avec le compilateur C# de .NET Framework 4 (C# 5).

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("StretchRes")]
[assembly: System.Reflection.AssemblyProduct("StretchRes")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]
[assembly: System.Reflection.AssemblyFileVersion("1.1.0.0")]

namespace StretchRes
{
    // ------------------------------------------------------------------ Win32
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DEVMODE
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmDeviceName;
        public short dmSpecVersion, dmDriverVersion, dmSize, dmDriverExtra;
        public int dmFields;
        public int dmPositionX, dmPositionY;
        public int dmDisplayOrientation;
        public int dmDisplayFixedOutput;
        public short dmColor, dmDuplex, dmYResolution, dmTTOption, dmCollate;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string dmFormName;
        public short dmLogPixels;
        public int dmBitsPerPel, dmPelsWidth, dmPelsHeight, dmDisplayFlags, dmDisplayFrequency;
        public int dmICMMethod, dmICMIntent, dmMediaType, dmDitherType, dmReserved1, dmReserved2, dmPanningWidth, dmPanningHeight;
    }

    static class Native
    {
        public const int ENUM_CURRENT_SETTINGS = -1, ENUM_REGISTRY_SETTINGS = -2;
        public const int DM_PELSWIDTH = 0x80000, DM_PELSHEIGHT = 0x100000, DM_DISPLAYFREQUENCY = 0x400000, DM_DISPLAYFIXEDOUTPUT = 0x20000000;
        public const int CDS_FULLSCREEN = 0x4;
        public const int DMDFO_STRETCH = 2;

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern bool EnumDisplaySettings(string deviceName, int modeNum, ref DEVMODE dm);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern int ChangeDisplaySettingsEx(string deviceName, ref DEVMODE dm, IntPtr hwnd, int flags, IntPtr lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "ChangeDisplaySettingsExW")]
        public static extern int ChangeDisplaySettingsExReset(string deviceName, IntPtr dm, IntPtr hwnd, int flags, IntPtr lParam);

        // --- CCD (SetDisplayConfig) : permet de garder le signal natif et de faire etirer par la carte graphique
        public const uint QDC_ONLY_ACTIVE_PATHS = 0x2;
        public const uint SDC_USE_DATABASE_CURRENT = 0xF, SDC_USE_SUPPLIED_DISPLAY_CONFIG = 0x20, SDC_VALIDATE = 0x40,
                          SDC_APPLY = 0x80, SDC_ALLOW_CHANGES = 0x400;
        public const uint SCALING_STRETCHED = 3;
        public const uint MODE_SOURCE = 1;

        [DllImport("user32.dll")]
        public static extern int GetDisplayConfigBufferSizes(uint flags, out uint numPaths, out uint numModes);
        [DllImport("user32.dll")]
        public static extern int QueryDisplayConfig(uint flags, ref uint numPaths, [Out] DISPLAYCONFIG_PATH_INFO[] paths,
            ref uint numModes, [Out] DISPLAYCONFIG_MODE_INFO[] modes, IntPtr topologyId);
        [DllImport("user32.dll")]
        public static extern int SetDisplayConfig(uint numPaths, [In] DISPLAYCONFIG_PATH_INFO[] paths,
            uint numModes, [In] DISPLAYCONFIG_MODE_INFO[] modes, uint flags);
        [DllImport("user32.dll")]
        public static extern int DisplayConfigGetDeviceInfo(ref DISPLAYCONFIG_SOURCE_DEVICE_NAME req);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        public static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr wParam, string lParam);

        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);

        // --- liste des processus, version legere (pas d'objets Process : ~10x moins cher)
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct PROCESSENTRY32
        {
            public uint dwSize, cntUsage, th32ProcessID;
            public IntPtr th32DefaultHeapID;
            public uint th32ModuleID, cntThreads, th32ParentProcessID;
            public int pcPriClassBase;
            public uint dwFlags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
        }
        [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW")] static extern bool Process32First(IntPtr h, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW")] static extern bool Process32Next(IntPtr h, ref PROCESSENTRY32 e);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern uint WaitForSingleObject(IntPtr h, uint ms);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern bool QueryFullProcessImageName(IntPtr h, uint flags, StringBuilder name, ref uint size);
        [DllImport("psapi.dll")] static extern bool EmptyWorkingSet(IntPtr hProcess);

        public static HashSet<string> ProcessNames()
        {
            HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            IntPtr snap = CreateToolhelp32Snapshot(0x2, 0);
            if (snap == IntPtr.Zero || snap == new IntPtr(-1)) return names;
            try
            {
                PROCESSENTRY32 e = new PROCESSENTRY32();
                e.dwSize = (uint)Marshal.SizeOf(typeof(PROCESSENTRY32));
                if (Process32First(snap, ref e))
                    do { names.Add(Game.Strip(e.szExeFile)); } while (Process32Next(snap, ref e));
            }
            finally { CloseHandle(snap); }
            return names;
        }

        public static string ProcessNameOf(uint pid)
        {
            IntPtr h = OpenProcess(0x1000 /* QUERY_LIMITED_INFORMATION */, false, pid);
            if (h == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                uint size = (uint)sb.Capacity;
                return QueryFullProcessImageName(h, 0, sb, ref size) ? Path.GetFileNameWithoutExtension(sb.ToString()) : null;
            }
            finally { CloseHandle(h); }
        }

        // Rend au systeme la memoire inutilisee (apres le scan, quand la fenetre est fermee...)
        public static void TrimMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                EmptyWorkingSet(Process.GetCurrentProcess().Handle);
            }
            catch { }
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct LUID { public uint LowPart; public int HighPart; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_SOURCE_INFO { public LUID adapterId; public uint id; public uint modeInfoIdx; public uint statusFlags; }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_TARGET_INFO
    {
        public LUID adapterId; public uint id; public uint modeInfoIdx; public uint outputTechnology; public uint rotation;
        public uint scaling; public uint refreshNum, refreshDen; public uint scanLineOrdering; public int targetAvailable; public uint statusFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct DISPLAYCONFIG_PATH_INFO { public DISPLAYCONFIG_PATH_SOURCE_INFO sourceInfo; public DISPLAYCONFIG_PATH_TARGET_INFO targetInfo; public uint flags; }

    [StructLayout(LayoutKind.Explicit, Size = 64)]
    struct DISPLAYCONFIG_MODE_INFO
    {
        [FieldOffset(0)] public uint infoType;
        [FieldOffset(4)] public uint id;
        [FieldOffset(8)] public LUID adapterId;
        // union : on garde les 48 octets intacts, et on lit/ecrit la partie "source mode"
        [FieldOffset(16)] public ulong raw0; [FieldOffset(24)] public ulong raw1; [FieldOffset(32)] public ulong raw2;
        [FieldOffset(40)] public ulong raw3; [FieldOffset(48)] public ulong raw4; [FieldOffset(56)] public ulong raw5;
        [FieldOffset(16)] public uint srcWidth;
        [FieldOffset(20)] public uint srcHeight;
        [FieldOffset(24)] public uint srcPixelFormat;
        [FieldOffset(28)] public int srcX;
        [FieldOffset(32)] public int srcY;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct DISPLAYCONFIG_SOURCE_DEVICE_NAME
    {
        public uint type; public uint size; public LUID adapterId; public uint id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string viewGdiDeviceName;
    }

    // ------------------------------------------------------------------ Langues
    // Chaque texte : { fr, en, es, ru, ja, zh }. Pour ajouter une langue : un code dans Codes,
    // son nom dans Names, puis une traduction a la meme position dans chaque ligne de D.
    static class L
    {
        public static readonly string[] Codes = { "fr", "en", "es", "ru", "ja", "zh" };
        public static readonly string[] Names = { "Français", "English", "Español", "Русский", "日本語", "中文" };
        static int idx;

        public static string Code { get { return Codes[idx]; } }
        public static int Index { get { return idx; } }

        public static bool Set(string code)
        {
            int i = Array.IndexOf(Codes, code ?? "");
            if (i < 0) return false;
            idx = i;
            return true;
        }

        // Langue de Windows si elle est disponible, sinon anglais.
        public static string Detect()
        {
            string two = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return Array.IndexOf(Codes, two) >= 0 ? two : "en";
        }

        // Police adaptee a l'ecriture (les polices CJK de Windows ont la meme allure que Segoe UI).
        public static string FontName
        {
            get { return Code == "ja" ? "Yu Gothic UI" : Code == "zh" ? "Microsoft YaHei UI" : "Segoe UI"; }
        }

        public static string T(string key)
        {
            string[] a;
            if (!D.TryGetValue(key, out a)) return key;
            return a[idx] ?? a[1];
        }

        public static string F(string key, params object[] args) { return string.Format(T(key), args); }

        static readonly Dictionary<string, string[]> D = new Dictionary<string, string[]>
        {
            // --- fenetre principale
            { "title", new[] { "StretchRes — résolutions étirées pour tes jeux", "StretchRes — stretched resolutions for your games", "StretchRes — resoluciones estiradas para tus juegos", "StretchRes — растянутое разрешение для игр", "StretchRes — ゲーム用の引き伸ばし解像度", "StretchRes — 为你的游戏设置拉伸分辨率" } },
            { "subtitle", new[] { "Tes jeux passent tout seuls au bon format, puis l'écran revient à la normale.", "Your games switch to the right format on their own, then the screen goes back to normal.", "Tus juegos cambian solos al formato correcto y luego la pantalla vuelve a la normalidad.", "Игры сами переходят в нужный формат, а потом экран возвращается к обычному.", "ゲームが自動で最適な解像度に切り替わり、終了すると画面は元に戻ります。", "游戏启动时自动切换到合适的分辨率，退出后屏幕恢复正常。" } },
            { "lang", new[] { "Langue", "Language", "Idioma", "Язык", "言語", "语言" } },
            { "my_games", new[] { "MES JEUX", "MY GAMES", "MIS JUEGOS", "МОИ ИГРЫ", "マイゲーム", "我的游戏" } },
            { "search", new[] { "Rechercher un jeu…  (Ctrl+F)", "Search for a game…  (Ctrl+F)", "Buscar un juego…  (Ctrl+F)", "Поиск игры…  (Ctrl+F)", "ゲームを検索…  (Ctrl+F)", "搜索游戏…  (Ctrl+F)" } },
            { "empty", new[] { "Aucun jeu pour l'instant.\nClique sur « Scanner mes jeux ».", "No games yet.\nClick “Scan my games”.", "Aún no hay juegos.\nHaz clic en «Buscar mis juegos».", "Игр пока нет.\nНажмите «Найти мои игры».", "まだゲームがありません。\n「ゲームをスキャン」をクリックしてください。", "还没有游戏。\n点击“扫描我的游戏”。" } },
            { "no_match", new[] { "Aucun jeu ne correspond à « {0} ».", "No game matches “{0}”.", "Ningún juego coincide con «{0}».", "Нет игр по запросу «{0}».", "「{0}」に一致するゲームはありません。", "没有与“{0}”匹配的游戏。" } },
            { "scan", new[] { "⟳  Scanner mes jeux", "⟳  Scan my games", "⟳  Buscar mis juegos", "⟳  Найти мои игры", "⟳  ゲームをスキャン", "⟳  扫描我的游戏" } },
            { "scanning", new[] { "Scan en cours…", "Scanning…", "Buscando…", "Поиск…", "スキャン中…", "正在扫描…" } },
            { "add", new[] { "+ Ajouter", "+ Add", "+ Añadir", "+ Добавить", "+ 追加", "+ 添加" } },
            { "remove", new[] { "Retirer", "Remove", "Quitar", "Убрать", "削除", "移除" } },
            { "settings", new[] { "RÉGLAGES DU JEU", "GAME SETTINGS", "AJUSTES DEL JUEGO", "НАСТРОЙКИ ИГРЫ", "ゲーム設定", "游戏设置" } },
            { "name", new[] { "Nom", "Name", "Nombre", "Название", "名前", "名称" } },
            { "watched", new[] { "Programme surveillé", "Watched program", "Programa vigilado", "Отслеживаемая программа", "監視するプログラム", "监视的程序" } },
            { "format", new[] { "Format quand le jeu est lancé", "Resolution while the game is running", "Resolución mientras el juego está abierto", "Разрешение во время игры", "ゲーム起動中の解像度", "游戏运行时的分辨率" } },
            { "enabled", new[] { "Activé pour ce jeu", "Enabled for this game", "Activado para este juego", "Включено для этой игры", "このゲームで有効", "对此游戏启用" } },
            { "test", new[] { "Tester 10 secondes", "Test for 10 seconds", "Probar 10 segundos", "Тест на 10 секунд", "10秒間テスト", "测试 10 秒" } },
            { "test_back", new[] { "Retour dans {0} s (arrêter)", "Back in {0} s (stop)", "Vuelve en {0} s (detener)", "Возврат через {0} с (стоп)", "あと{0}秒で戻ります（停止）", "{0} 秒后恢复（停止）" } },
            { "hint", new[] {
                "Dans le jeu : mode « Plein écran fenêtré ». StretchRes demande à la carte graphique d'étirer l'image. S'il reste des bandes noires : Panneau NVIDIA → Ajuster la taille et la position du bureau → « Plein écran », mise à l'échelle sur « GPU », et décoche « Remplacer le mode défini par les jeux ».",
                "In the game: “Windowed fullscreen” mode. StretchRes asks the graphics card to stretch the image. If black bars remain: NVIDIA panel → Adjust desktop size and position → “Full-screen”, scaling on “GPU”, and uncheck “Override the scaling mode set by games”.",
                "En el juego: modo «Pantalla completa en ventana». StretchRes pide a la tarjeta gráfica que estire la imagen. Si quedan barras negras: panel NVIDIA → Ajustar el tamaño y la posición del escritorio → «Pantalla completa», escalado en «GPU», y desmarca «Anular el modo de escala de los juegos».",
                "В игре: режим «Оконный полноэкранный». StretchRes просит видеокарту растянуть картинку. Если остались чёрные полосы: панель NVIDIA → Регулировка размера и положения рабочего стола → «Во весь экран», масштабирование на «ГП», и снимите флажок «Замещение режима масштабирования игр».",
                "ゲーム内では「ボーダーレス（ウィンドウ化フルスクリーン）」を選択。StretchRes がグラフィックカードに画像の引き伸ばしを指示します。黒い帯が残る場合：NVIDIA コントロールパネル → デスクトップのサイズと位置の調整 →「フルスクリーン」、スケーリングを「GPU」で実行し、「ゲームで設定されたスケーリングモードを上書き」のチェックを外します。",
                "游戏中请选择“窗口化全屏（无边框）”模式。StretchRes 会让显卡拉伸画面。如果仍有黑边：NVIDIA 控制面板 → 调整桌面尺寸和位置 → “全屏”，在“GPU”上执行缩放，并取消勾选“覆盖由游戏设置的缩放模式”。" } },
            { "startup", new[] { "Lancer avec Windows", "Start with Windows", "Iniciar con Windows", "Запуск с Windows", "Windows 起動時に実行", "开机自启动" } },
            { "stretch", new[] { "Étirer l'image", "Stretch the image", "Estirar la imagen", "Растягивать картинку", "画像を引き伸ばす", "拉伸画面" } },
            { "alttab", new[] { "Écran normal pendant Alt+Tab", "Normal screen during Alt+Tab", "Pantalla normal durante Alt+Tab", "Обычный экран при Alt+Tab", "Alt+Tab 中は通常の画面", "Alt+Tab 时恢复正常屏幕" } },
            { "uninstall", new[] { "Désinstaller", "Uninstall", "Desinstalar", "Удалить", "アンインストール", "卸载" } },
            { "uninstall_q", new[] { "Désinstaller StretchRes ?\nIl ne se lancera plus au démarrage et tes réglages seront supprimés.", "Uninstall StretchRes?\nIt will no longer start with Windows and your settings will be deleted.", "¿Desinstalar StretchRes?\nYa no se iniciará con Windows y se borrarán tus ajustes.", "Удалить StretchRes?\nПрограмма больше не будет запускаться с Windows, а настройки будут удалены.", "StretchRes をアンインストールしますか？\nWindows 起動時に実行されなくなり、設定は削除されます。", "要卸载 StretchRes 吗？\n它将不再随 Windows 启动，你的设置也会被删除。" } },

            // --- liste des formats
            { "tag_native", new[] { "natif", "native", "nativa", "родное", "ネイティブ", "原生" } },
            { "tag_pro", new[] { "étiré pro", "pro stretched", "estirada pro", "растянутое (про)", "プロ向け引き伸ばし", "职业拉伸" } },
            { "tag_stretched", new[] { "étiré", "stretched", "estirada", "растянутое", "引き伸ばし", "拉伸" } },
            { "to_create", new[] { "à créer dans NVIDIA", "create it in NVIDIA", "crear en NVIDIA", "создать в NVIDIA", "NVIDIA で作成が必要", "需在 NVIDIA 中创建" } },
            { "native_item", new[] { "Natif — ne rien changer ({0} × {1})", "Native — don't change anything ({0} × {1})", "Nativa — no cambiar nada ({0} × {1})", "Родное — ничего не менять ({0} × {1})", "ネイティブ — 変更しない ({0} × {1})", "原生 — 不做更改 ({0} × {1})" } },
            { "custom", new[] { "personnalisée", "custom", "personalizada", "своё", "カスタム", "自定义" } },
            { "other_res", new[] { "Autre résolution…", "Other resolution…", "Otra resolución…", "Другое разрешение…", "その他の解像度…", "其他分辨率…" } },
            { "native_short", new[] { "Natif", "Native", "Nativa", "Родное", "ネイティブ", "原生" } },
            { "fav_remove", new[] { "Retirer des favoris", "Remove from favorites", "Quitar de favoritos", "Убрать из избранного", "お気に入りから削除", "从收藏中移除" } },
            { "fav_add", new[] { "Ajouter aux favoris (en haut de la liste)", "Add to favorites (top of the list)", "Añadir a favoritos (arriba de la lista)", "В избранное (вверху списка)", "お気に入りに追加（リストの先頭に表示）", "加入收藏（显示在列表顶部）" } },
            { "pill_playing", new[] { "EN JEU", "PLAYING", "JUGANDO", "В ИГРЕ", "プレイ中", "游戏中" } },
            { "pill_off", new[] { "DÉSACTIVÉ", "OFF", "DESACTIVADO", "ВЫКЛ.", "無効", "已禁用" } },

            // --- dialogues
            { "custom_res", new[] { "Résolution personnalisée", "Custom resolution", "Resolución personalizada", "Своё разрешение", "カスタム解像度", "自定义分辨率" } },
            { "width", new[] { "Largeur", "Width", "Ancho", "Ширина", "幅", "宽度" } },
            { "height", new[] { "Hauteur", "Height", "Alto", "Высота", "高さ", "高度" } },
            { "menu_browse", new[] { "Choisir le fichier .exe du jeu…", "Choose the game's .exe file…", "Elegir el archivo .exe del juego…", "Выбрать .exe-файл игры…", "ゲームの .exe ファイルを選択…", "选择游戏的 .exe 文件…" } },
            { "menu_popular", new[] { "Jeux populaires", "Popular games", "Juegos populares", "Популярные игры", "人気のゲーム", "热门游戏" } },
            { "menu_running", new[] { "Un programme ouvert en ce moment", "A program that's running now", "Un programa abierto ahora", "Программа, открытая сейчас", "現在開いているプログラム", "当前打开的程序" } },
            { "none", new[] { "(aucun)", "(none)", "(ninguno)", "(нет)", "（なし）", "（无）" } },
            { "menu_type", new[] { "Taper le nom du .exe…", "Type the .exe name…", "Escribir el nombre del .exe…", "Ввести имя .exe…", ".exe 名を入力…", "输入 .exe 名称…" } },
            { "csgo_old", new[] { "CS:GO (ancien)", "CS:GO (legacy)", "CS:GO (antiguo)", "CS:GO (старый)", "CS:GO（旧版）", "CS:GO（旧版）" } },
            { "browse_title", new[] { "Choisis le .exe du jeu", "Choose the game's .exe", "Elige el .exe del juego", "Выберите .exe игры", "ゲームの .exe を選択", "选择游戏的 .exe" } },
            { "browse_filter", new[] { "Programmes (*.exe)|*.exe", "Programs (*.exe)|*.exe", "Programas (*.exe)|*.exe", "Программы (*.exe)|*.exe", "プログラム (*.exe)|*.exe", "程序 (*.exe)|*.exe" } },
            { "type_title", new[] { "Nom du programme", "Program name", "Nombre del programa", "Имя программы", "プログラム名", "程序名称" } },
            { "type_label", new[] { "Nom du .exe (ex : cs2.exe)", ".exe name (e.g. cs2.exe)", "Nombre del .exe (ej.: cs2.exe)", "Имя .exe (например, cs2.exe)", ".exe 名（例：cs2.exe）", ".exe 名称（例如 cs2.exe）" } },
            { "add_btn", new[] { "Ajouter", "Add", "Añadir", "Добавить", "追加", "添加" } },

            // --- icone pres de l'horloge
            { "installed_title", new[] { "StretchRes est installé ✓", "StretchRes is installed ✓", "StretchRes está instalado ✓", "StretchRes установлен ✓", "StretchRes をインストールしました ✓", "StretchRes 已安装 ✓" } },
            { "installed_body", new[] { "Il se lancera discrètement à chaque démarrage. Clique sur l'icône orange près de l'horloge pour choisir tes jeux.", "It will start quietly with Windows. Click the orange icon next to the clock to choose your games.", "Se iniciará discretamente con Windows. Haz clic en el icono naranja junto al reloj para elegir tus juegos.", "Программа будет тихо запускаться с Windows. Нажмите оранжевый значок у часов, чтобы выбрать игры.", "Windows 起動時にバックグラウンドで実行されます。時計の横のオレンジ色のアイコンをクリックしてゲームを選びましょう。", "它会随 Windows 在后台启动。点击时钟旁的橙色图标来选择你的游戏。" } },
            { "menu_open", new[] { "Ouvrir StretchRes", "Open StretchRes", "Abrir StretchRes", "Открыть StretchRes", "StretchRes を開く", "打开 StretchRes" } },
            { "menu_detect", new[] { "Détection automatique des jeux", "Automatic game detection", "Detección automática de juegos", "Автоопределение игр", "ゲームの自動検出", "自动检测游戏" } },
            { "menu_restore", new[] { "Remettre la résolution normale", "Restore normal resolution", "Restaurar la resolución normal", "Вернуть обычное разрешение", "通常の解像度に戻す", "恢复正常分辨率" } },
            { "menu_quit", new[] { "Quitter", "Quit", "Salir", "Выход", "終了", "退出" } },
            { "bg_title", new[] { "StretchRes tourne en arrière-plan", "StretchRes is running in the background", "StretchRes sigue funcionando en segundo plano", "StretchRes работает в фоне", "StretchRes はバックグラウンドで動作中です", "StretchRes 正在后台运行" } },
            { "bg_body", new[] { "Tes jeux passeront automatiquement à leur format. Clic droit sur l'icône pour quitter.", "Your games will switch to their format automatically. Right-click the icon to quit.", "Tus juegos cambiarán a su formato automáticamente. Clic derecho en el icono para salir.", "Игры будут сами переходить в свой формат. Чтобы выйти, щёлкните значок правой кнопкой.", "ゲームは自動で設定した解像度に切り替わります。終了するにはアイコンを右クリックします。", "游戏会自动切换到设定的分辨率。右键点击图标即可退出。" } },
            { "test_native", new[] { "Ce jeu est en format natif : rien à tester.", "This game uses the native format: nothing to test.", "Este juego usa el formato nativo: no hay nada que probar.", "Для этой игры выбрано родное разрешение: тестировать нечего.", "このゲームはネイティブ解像度のため、テストするものはありません。", "此游戏使用原生分辨率，无需测试。" } },
            { "scan_added", new[] { "{0} jeu(x) ajouté(s) à StretchRes", "{0} game(s) added to StretchRes", "{0} juego(s) añadido(s) a StretchRes", "Добавлено игр в StretchRes: {0}", "StretchRes に {0} 本のゲームを追加しました", "已向 StretchRes 添加 {0} 个游戏" } },
            { "scan_added_body", new[] { "Choisis leur format dans la fenêtre.", "Choose their format in the window.", "Elige su formato en la ventana.", "Выберите для них формат в окне.", "ウィンドウで解像度を選んでください。", "请在窗口中选择它们的分辨率。" } },
            { "scan_none", new[] { "Aucun nouveau jeu trouvé.\nTu peux toujours en ajouter un à la main avec « + Ajouter ».", "No new games found.\nYou can still add one manually with “+ Add”.", "No se encontraron juegos nuevos.\nPuedes añadir uno a mano con «+ Añadir».", "Новых игр не найдено.\nИгру можно добавить вручную кнопкой «+ Добавить».", "新しいゲームは見つかりませんでした。\n「+ 追加」から手動で追加できます。", "没有找到新游戏。\n你可以用“+ 添加”手动添加。" } },
            { "st_test", new[] { "Test en cours → {0} × {1}", "Testing → {0} × {1}", "Probando → {0} × {1}", "Тест → {0} × {1}", "テスト中 → {0} × {1}", "测试中 → {0} × {1}" } },
            { "st_bg", new[] { "{0} en arrière-plan → écran normal", "{0} in background → normal screen", "{0} en segundo plano → pantalla normal", "{0} в фоне → обычный экран", "{0} はバックグラウンド → 通常画面", "{0} 在后台 → 正常屏幕" } },
            { "st_native", new[] { "{0} → natif", "{0} → native", "{0} → nativa", "{0} → родное", "{0} → ネイティブ", "{0} → 原生" } },
            { "st_refused", new[] { "{0} : résolution refusée", "{0}: resolution rejected", "{0}: resolución rechazada", "{0}: разрешение отклонено", "{0}：解像度を適用できません", "{0}：分辨率被拒绝" } },
            { "st_waiting", new[] { "En attente d'un jeu…", "Waiting for a game…", "Esperando un juego…", "Ожидание игры…", "ゲームを待機中…", "等待游戏…" } },
            { "st_off", new[] { "Détection désactivée", "Detection off", "Detección desactivada", "Определение выключено", "検出オフ", "检测已关闭" } },

            // --- erreurs
            { "err_missing", new[] {
                "La résolution {0} × {1} n'existe pas encore sur ta carte graphique.\n\nPour la créer : Panneau de configuration NVIDIA → Modifier la résolution → Personnaliser… → coche « Activer les résolutions non exposées par l'affichage » → Créer une résolution personnalisée → {0} × {1} → Tester → Oui. Ensuite, réessaie dans StretchRes.",
                "The resolution {0} × {1} doesn't exist on your graphics card yet.\n\nTo create it: NVIDIA Control Panel → Change resolution → Customize… → check “Enable resolutions not exposed by the display” → Create Custom Resolution → {0} × {1} → Test → Yes. Then try again in StretchRes.",
                "La resolución {0} × {1} aún no existe en tu tarjeta gráfica.\n\nPara crearla: Panel de control de NVIDIA → Cambiar resolución → Personalizar… → marca «Activar resoluciones no expuestas por la pantalla» → Crear resolución personalizada → {0} × {1} → Probar → Sí. Luego vuelve a intentarlo en StretchRes.",
                "Разрешения {0} × {1} пока нет на вашей видеокарте.\n\nКак его создать: Панель управления NVIDIA → Изменение разрешения → Настройка… → отметьте «Включить режимы, не предлагаемые дисплеем» → Создать пользовательское разрешение → {0} × {1} → Тест → Да. Затем попробуйте снова в StretchRes.",
                "解像度 {0} × {1} はまだグラフィックカードにありません。\n\n作成方法：NVIDIA コントロールパネル → 解像度の変更 → カスタマイズ… →「ディスプレイで公開されていない解像度を有効にする」にチェック → カスタム解像度の作成 → {0} × {1} → テスト → はい。その後、StretchRes でもう一度お試しください。",
                "你的显卡上还没有 {0} × {1} 分辨率。\n\n创建方法：NVIDIA 控制面板 → 更改分辨率 → 自定义… → 勾选“启用显示器未显示的分辨率” → 创建自定义分辨率 → {0} × {1} → 测试 → 是。然后在 StretchRes 中重试。" } },
            { "err_reboot", new[] { "Windows demande un redémarrage pour appliquer cette résolution.", "Windows needs a restart to apply this resolution.", "Windows necesita reiniciar para aplicar esta resolución.", "Чтобы применить это разрешение, нужно перезагрузить Windows.", "この解像度を適用するには Windows の再起動が必要です。", "需要重启 Windows 才能应用此分辨率。" } },
            { "err_code", new[] { "Impossible de changer la résolution (code {0}).", "Couldn't change the resolution (code {0}).", "No se pudo cambiar la resolución (código {0}).", "Не удалось изменить разрешение (код {0}).", "解像度を変更できませんでした（コード {0}）。", "无法更改分辨率（代码 {0}）。" } },
            { "err_close_old", new[] { "Impossible de fermer l'ancienne version de StretchRes. Quitte-la (clic droit sur l'icône → Quitter) puis relance ce fichier.", "Couldn't close the old version of StretchRes. Quit it (right-click the icon → Quit), then run this file again.", "No se pudo cerrar la versión anterior de StretchRes. Ciérrala (clic derecho en el icono → Salir) y vuelve a abrir este archivo.", "Не удалось закрыть старую версию StretchRes. Закройте её (правый щелчок по значку → Выход) и снова запустите этот файл.", "古いバージョンの StretchRes を終了できませんでした。アイコンを右クリック →「終了」で閉じてから、このファイルをもう一度実行してください。", "无法关闭旧版 StretchRes。请先退出它（右键点击图标 → 退出），然后重新运行此文件。" } },
            { "err_install", new[] { "Installation impossible : {0}", "Installation failed: {0}", "No se pudo instalar: {0}", "Не удалось установить: {0}", "インストールできませんでした：{0}", "安装失败：{0}" } },
        };
    }

    // Choix de la langue au premier lancement : un bouton par langue, celle de Windows en orange.
    static class LangPicker
    {
        public static Form Create(string preselect, Action<string> chosen)
        {
            Form f = new Form();
            f.Text = "StretchRes";
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = false; f.MinimizeBox = false;
            f.StartPosition = FormStartPosition.CenterScreen;
            f.ClientSize = new Size(380, 236);
            f.BackColor = Theme.Bg; f.ForeColor = Theme.Text;
            f.Font = Theme.UI(9.5f);
            f.Icon = Theme.AppIcon;
            f.Controls.Add(Theme.MakeLabel("StretchRes", 20, 12, Theme.Text, 16f, FontStyle.Bold));
            Label sub = Theme.MakeLabel("Langue · Language · Idioma · Язык · 言語 · 语言", 22, 48, Theme.Dim, 9f, FontStyle.Regular);
            f.Controls.Add(sub);
            for (int i = 0; i < L.Codes.Length; i++)
            {
                string code = L.Codes[i];
                Button b = Theme.MakeButton(L.Names[i], code == preselect);
                b.Location = new Point(20 + (i % 2) * 174, 80 + (i / 2) * 48);
                b.Size = new Size(166, 40);
                b.Click += (s, e) => { chosen(code); f.DialogResult = DialogResult.OK; f.Close(); };
                f.Controls.Add(b);
                if (code == preselect) f.Shown += (s, e) => b.Focus();
            }
            return f;
        }

        public static string Ask(string preselect)
        {
            string result = preselect;
            using (Form f = Create(preselect, c => result = c))
            {
                f.TopMost = true;
                f.ShowDialog();
            }
            return result;
        }
    }

    // ------------------------------------------------------------------ Display
    class Mode
    {
        public int W, H;
        public List<int> Rates = new List<int>();
    }

    static class Display
    {
        public static bool IsChanged;

        public static string Device { get { return Screen.PrimaryScreen.DeviceName; } }

        static DEVMODE NewDM()
        {
            DEVMODE d = new DEVMODE();
            d.dmDeviceName = ""; d.dmFormName = "";
            d.dmSize = (short)Marshal.SizeOf(typeof(DEVMODE));
            return d;
        }

        public static Size NativeSize()
        {
            DEVMODE d = NewDM();
            if (Native.EnumDisplaySettings(Device, Native.ENUM_REGISTRY_SETTINGS, ref d)) return new Size(d.dmPelsWidth, d.dmPelsHeight);
            return Screen.PrimaryScreen.Bounds.Size;
        }

        public static DEVMODE Current()
        {
            DEVMODE d = NewDM();
            Native.EnumDisplaySettings(Device, Native.ENUM_CURRENT_SETTINGS, ref d);
            return d;
        }

        public static List<Mode> Modes()
        {
            Dictionary<long, Mode> dict = new Dictionary<long, Mode>();
            DEVMODE dm = NewDM();
            int i = 0;
            while (Native.EnumDisplaySettings(Device, i++, ref dm))
            {
                if (dm.dmBitsPerPel < 32 || dm.dmPelsWidth < 640 || dm.dmPelsHeight < 480) continue;
                long k = dm.dmPelsWidth * 100000L + dm.dmPelsHeight;
                Mode m;
                if (!dict.TryGetValue(k, out m)) { m = new Mode { W = dm.dmPelsWidth, H = dm.dmPelsHeight }; dict[k] = m; }
                if (!m.Rates.Contains(dm.dmDisplayFrequency)) m.Rates.Add(dm.dmDisplayFrequency);
            }
            return dict.Values.ToList();
        }

        static DISPLAYCONFIG_PATH_INFO[] savedPaths;
        static DISPLAYCONFIG_MODE_INFO[] savedModes;
        static bool usedCcd;
        public static string LastMethod = "";

        static bool QueryConfig(out DISPLAYCONFIG_PATH_INFO[] paths, out DISPLAYCONFIG_MODE_INFO[] modes)
        {
            paths = null; modes = null;
            uint np, nm;
            if (Native.GetDisplayConfigBufferSizes(Native.QDC_ONLY_ACTIVE_PATHS, out np, out nm) != 0) return false;
            paths = new DISPLAYCONFIG_PATH_INFO[np];
            modes = new DISPLAYCONFIG_MODE_INFO[nm];
            if (Native.QueryDisplayConfig(Native.QDC_ONLY_ACTIVE_PATHS, ref np, paths, ref nm, modes, IntPtr.Zero) != 0) return false;
            Array.Resize(ref paths, (int)np);
            Array.Resize(ref modes, (int)nm);
            return true;
        }

        static string SourceName(DISPLAYCONFIG_PATH_INFO p)
        {
            DISPLAYCONFIG_SOURCE_DEVICE_NAME r = new DISPLAYCONFIG_SOURCE_DEVICE_NAME();
            r.type = 1; // GET_SOURCE_NAME
            r.size = (uint)Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME));
            r.adapterId = p.sourceInfo.adapterId;
            r.id = p.sourceInfo.id;
            r.viewGdiDeviceName = "";
            return Native.DisplayConfigGetDeviceInfo(ref r) == 0 ? r.viewGdiDeviceName : "";
        }

        // Change uniquement l'image du bureau (source) et laisse le signal envoye a l'ecran en natif :
        // la carte graphique est alors obligee d'etirer l'image en plein ecran (pas de bandes noires).
        static int ApplyCcd(int w, int h, bool validateOnly)
        {
            DISPLAYCONFIG_PATH_INFO[] paths; DISPLAYCONFIG_MODE_INFO[] modes;
            if (!QueryConfig(out paths, out modes)) return -100;
            if (!IsChanged && !validateOnly) { savedPaths = (DISPLAYCONFIG_PATH_INFO[])paths.Clone(); savedModes = (DISPLAYCONFIG_MODE_INFO[])modes.Clone(); }

            int pi = -1;
            for (int i = 0; i < paths.Length; i++)
                if (string.Equals(SourceName(paths[i]), Device, StringComparison.OrdinalIgnoreCase)) { pi = i; break; }
            if (pi < 0) return -101;
            uint si = paths[pi].sourceInfo.modeInfoIdx;
            if (si >= modes.Length || modes[si].infoType != Native.MODE_SOURCE) return -102;

            int oldW = (int)modes[si].srcWidth, oldH = (int)modes[si].srcHeight;
            modes[si].srcWidth = (uint)w;
            modes[si].srcHeight = (uint)h;
            // decale les autres ecrans pour garder un bureau continu
            for (int i = 0; i < modes.Length; i++)
            {
                if (i == si || modes[i].infoType != Native.MODE_SOURCE) continue;
                if (modes[i].srcX >= oldW) modes[i].srcX -= oldW - w;
                if (modes[i].srcY >= oldH) modes[i].srcY -= oldH - h;
            }
            for (int i = 0; i < paths.Length; i++)
                if (paths[i].sourceInfo.modeInfoIdx == si) paths[i].targetInfo.scaling = Native.SCALING_STRETCHED;

            uint flags = Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | (validateOnly ? Native.SDC_VALIDATE : Native.SDC_APPLY | Native.SDC_ALLOW_CHANGES);
            return Native.SetDisplayConfig((uint)paths.Length, paths, (uint)modes.Length, modes, flags);
        }

        public static int ValidateStretch(int w, int h) { return ApplyCcd(w, h, true); }

        public static string CurrentScaling()
        {
            DISPLAYCONFIG_PATH_INFO[] paths; DISPLAYCONFIG_MODE_INFO[] modes;
            if (!QueryConfig(out paths, out modes)) return "?";
            foreach (DISPLAYCONFIG_PATH_INFO p in paths)
                if (string.Equals(SourceName(p), Device, StringComparison.OrdinalIgnoreCase))
                {
                    switch (p.targetInfo.scaling)
                    {
                        case 1: return "identité"; case 2: return "centré"; case 3: return "étiré";
                        case 4: return "proportions conservées"; case 5: return "personnalisé"; case 128: return "préféré (pilote)";
                        default: return p.targetInfo.scaling.ToString();
                    }
                }
            return "?";
        }

        // Renvoie null si OK, sinon un message d'erreur.
        public static string Apply(int w, int h, bool stretch)
        {
            if (stretch)
            {
                int rc = ApplyCcd(w, h, false);
                DEVMODE now = Current();
                if (rc == 0 && now.dmPelsWidth == w && now.dmPelsHeight == h)
                {
                    IsChanged = true; usedCcd = true; LastMethod = "carte graphique (étiré)";
                    return null;
                }
                if (rc == 0) { IsChanged = true; usedCcd = true; }
                LastMethod = "classique (code CCD " + rc + ")";
            }
            DEVMODE cur = Current();
            int hz = cur.dmDisplayFrequency;
            Mode m = Modes().FirstOrDefault(x => x.W == w && x.H == h);
            if (m != null && m.Rates.Count > 0 && !m.Rates.Contains(hz)) hz = m.Rates.Max();

            DEVMODE dm = cur;
            dm.dmPelsWidth = w; dm.dmPelsHeight = h; dm.dmDisplayFrequency = hz;
            dm.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT | Native.DM_DISPLAYFREQUENCY;

            int r = -1;
            if (stretch)
            {
                DEVMODE s = dm;
                s.dmFields |= Native.DM_DISPLAYFIXEDOUTPUT;
                s.dmDisplayFixedOutput = Native.DMDFO_STRETCH;
                r = Native.ChangeDisplaySettingsEx(Device, ref s, IntPtr.Zero, Native.CDS_FULLSCREEN, IntPtr.Zero);
            }
            if (r != 0) r = Native.ChangeDisplaySettingsEx(Device, ref dm, IntPtr.Zero, Native.CDS_FULLSCREEN, IntPtr.Zero);
            if (r != 0)
            {
                DEVMODE d2 = cur;
                d2.dmPelsWidth = w; d2.dmPelsHeight = h;
                d2.dmFields = Native.DM_PELSWIDTH | Native.DM_PELSHEIGHT;
                r = Native.ChangeDisplaySettingsEx(Device, ref d2, IntPtr.Zero, Native.CDS_FULLSCREEN, IntPtr.Zero);
            }
            if (r == 0) { IsChanged = true; return null; }
            if (r == -2 || r == -1) return L.F("err_missing", w, h);
            if (r == 1) return L.T("err_reboot");
            return L.F("err_code", r);
        }

        // Remet l'ecran normal meme si ce n'est pas cette instance qui l'a change.
        public static void ForceReset()
        {
            IsChanged = true; usedCcd = true;
            Restore();
        }

        public static bool AtNative()
        {
            Size n = NativeSize();
            DEVMODE c = Current();
            return c.dmPelsWidth == n.Width && c.dmPelsHeight == n.Height;
        }

        public static void Restore()
        {
            if (!IsChanged) return;
            if (usedCcd)
            {
                // 1) reprend la configuration enregistree par Windows (celle de tous les jours)
                int r = Native.SetDisplayConfig(0, null, 0, null, Native.SDC_APPLY | Native.SDC_USE_DATABASE_CURRENT);
                // 2) sinon remet exactement la configuration d'avant
                if ((r != 0 || !AtNative()) && savedPaths != null)
                    Native.SetDisplayConfig((uint)savedPaths.Length, savedPaths, (uint)savedModes.Length, savedModes,
                        Native.SDC_APPLY | Native.SDC_USE_SUPPLIED_DISPLAY_CONFIG | Native.SDC_ALLOW_CHANGES);
            }
            // 3) dernier filet de securite
            if (!usedCcd || !AtNative())
                Native.ChangeDisplaySettingsExReset(Device, IntPtr.Zero, IntPtr.Zero, 0, IntPtr.Zero);
            IsChanged = false; usedCcd = false; savedPaths = null; savedModes = null;
        }

        public static string Ratio(int w, int h)
        {
            double r = (double)w / h;
            string[] names = { "4:3", "5:4", "3:2", "16:10", "16:9", "21:9", "32:9" };
            double[] vals = { 4 / 3.0, 5 / 4.0, 3 / 2.0, 16 / 10.0, 16 / 9.0, 2.37, 32 / 9.0 };
            double[] tol = { 0.02, 0.02, 0.02, 0.02, 0.03, 0.08, 0.05 };
            for (int i = 0; i < names.Length; i++) if (Math.Abs(r - vals[i]) <= tol[i]) return names[i];
            return r.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture) + ":1";
        }

        public static int RatioOrder(int w, int h)
        {
            string r = Ratio(w, h);
            switch (r) { case "4:3": return 0; case "5:4": return 1; case "16:10": return 2; case "16:9": return 3; default: return 4; }
        }
    }

    // ------------------------------------------------------------------ Config
    class Game
    {
        public string Exe = "";
        public string Name = "";
        public int W, H;
        public bool Enabled = true;

        // Exe peut contenir plusieurs programmes separes par ';' (ex : r5apex.exe;r5apex_dx12.exe)
        public static string Strip(string exe)
        {
            string n = exe.Trim();
            if (n.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) n = n.Substring(0, n.Length - 4);
            return n;
        }

        string namesFor; string[] names; // cache : recalcule seulement si Exe change
        public string[] ProcNames
        {
            get
            {
                if (names == null || !ReferenceEquals(namesFor, Exe))
                {
                    names = Exe.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => Strip(x)).ToArray();
                    namesFor = Exe;
                }
                return names;
            }
        }

        public string ProcName { get { string[] p = ProcNames; return p.Length > 0 ? p[0] : ""; } }

        public bool Matches(string procName)
        {
            foreach (string p in ProcNames) if (string.Equals(p, procName, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }
    }

    class Config
    {
        public static bool NoSave;
        public List<Game> Games = new List<Game>();
        public HashSet<string> Ignored = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool Stretch = true, Detection = true, HintShown, NormalOnAltTab = true, Scanned;
        public string Lang = ""; // vide = pas encore choisie (on demande au premier lancement)
        // resolutions favorites (en tete de liste, avec une etoile)
        public List<Size> Favs = new List<Size> { new Size(1440, 1080), new Size(1600, 1080), new Size(1280, 960) };

        public bool IsFav(int w, int h) { return Favs.Any(f => f.Width == w && f.Height == h); }

        public bool Known(string exeList)
        {
            foreach (string e in exeList.Split(';'))
            {
                string p = Game.Strip(e);
                if (p.Length == 0) continue;
                if (Ignored.Contains(p)) return true;
                foreach (Game g in Games) if (g.Matches(p)) return true;
            }
            return false;
        }

        public void SortGames()
        {
            Games = Games.OrderBy(g => g.W > 0 ? 0 : 1).ThenBy(g => g.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        public static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StretchRes"); } }
        public static string FilePath { get { return Path.Combine(Dir, "config.txt"); } }

        public static Config Load()
        {
            Config c = new Config();
            try
            {
                if (!File.Exists(FilePath)) return c;
                foreach (string raw in File.ReadAllLines(FilePath, Encoding.UTF8))
                {
                    string line = raw.Trim();
                    int eq = line.IndexOf('=');
                    if (eq < 0) continue;
                    string k = line.Substring(0, eq), v = line.Substring(eq + 1);
                    if (k == "stretch") c.Stretch = v == "1";
                    else if (k == "detection") c.Detection = v == "1";
                    else if (k == "hint") c.HintShown = v == "1";
                    else if (k == "alttab") c.NormalOnAltTab = v == "1";
                    else if (k == "scanned") c.Scanned = v == "1";
                    else if (k == "lang") c.Lang = v.Trim();
                    else if (k == "favs")
                    {
                        c.Favs.Clear();
                        foreach (string part in v.Split(','))
                        {
                            string[] wh = part.Split('x');
                            int fw, fh;
                            if (wh.Length == 2 && int.TryParse(wh[0], out fw) && int.TryParse(wh[1], out fh)) c.Favs.Add(new Size(fw, fh));
                        }
                    }
                    else if (k == "ignore") { if (v.Trim().Length > 0) c.Ignored.Add(Game.Strip(v)); }
                    else if (k == "game")
                    {
                        string[] p = v.Split('|');
                        if (p.Length < 5) continue;
                        Game g = new Game { Enabled = p[0] == "1", Exe = p[1], Name = p[2].Trim() };
                        int.TryParse(p[3], out g.W); int.TryParse(p[4], out g.H);
                        if (g.Exe.Length > 0) c.Games.Add(g);
                    }
                }
            }
            catch { }
            return c;
        }

        public void Save()
        {
            if (NoSave) return;
            try
            {
                Directory.CreateDirectory(Dir);
                List<string> l = new List<string>();
                l.Add("stretch=" + (Stretch ? "1" : "0"));
                l.Add("detection=" + (Detection ? "1" : "0"));
                l.Add("hint=" + (HintShown ? "1" : "0"));
                l.Add("alttab=" + (NormalOnAltTab ? "1" : "0"));
                l.Add("scanned=" + (Scanned ? "1" : "0"));
                if (Lang.Length > 0) l.Add("lang=" + Lang);
                l.Add("favs=" + string.Join(",", Favs.Select(f => f.Width + "x" + f.Height)));
                foreach (string ig in Ignored) l.Add("ignore=" + ig);
                foreach (Game g in Games)
                    l.Add("game=" + (g.Enabled ? "1" : "0") + "|" + g.Exe.Replace("|", "") + "|" + g.Name.Replace("|", "/") + "|" + g.W + "|" + g.H);
                // ecriture sure : fichier temporaire puis remplacement (jamais de config a moitie ecrite)
                string tmp = FilePath + ".tmp";
                File.WriteAllLines(tmp, l.ToArray(), Encoding.UTF8);
                if (File.Exists(FilePath)) File.Replace(tmp, FilePath, null);
                else File.Move(tmp, FilePath);
            }
            catch { }
        }
    }

    // ------------------------------------------------------------------ Scan des jeux installes
    class FoundGame { public string Name, Exe; }

    static class Scanner
    {
        // mot-cle (nom du jeu / dossier) -> nom affiche, exe(s) du jeu
        public static readonly string[][] KnownGames =
        {
            new[] { "Fortnite", "Fortnite", "FortniteClient-Win64-Shipping.exe" },
            new[] { "Counter-Strike 2", "Counter-Strike 2", "cs2.exe" },
            new[] { "Counter-Strike Global Offensive", "Counter-Strike 2", "cs2.exe" },
            new[] { "VALORANT", "Valorant", "VALORANT-Win64-Shipping.exe" },
            new[] { "Apex Legends", "Apex Legends", "r5apex.exe;r5apex_dx12.exe" },
            new[] { "Rocket League", "Rocket League", "RocketLeague.exe" },
            new[] { "Rainbow Six", "Rainbow Six Siege", "RainbowSix.exe;RainbowSix_Vulkan.exe;RainbowSix_DX11.exe" },
            new[] { "Overwatch", "Overwatch 2", "Overwatch.exe" },
            new[] { "PUBG", "PUBG", "TslGame.exe" },
            new[] { "PLAYERUNKNOWN", "PUBG", "TslGame.exe" },
            new[] { "League of Legends", "League of Legends", "League of Legends.exe" },
            new[] { "Aimlabs", "Aimlabs", "AimLab_tb.exe" },
        };

        static readonly string[] BadExe =
        {
            "unins", "setup", "install", "crash", "report", "redist", "dxsetup", "helper", "eac", "battleye", "anticheat",
            "update", "patch", "config", "cefprocess", "webhelper", "benchmark", "server", "editor", "prereq", "dotnet",
            "vc_", "vcredist", "touchup", "cleanup", "uploader", "overlay", "register", "activation", "dxwebsetup",
            "launcherpatcher", "bootstrap", "unitycrash", "quicksfv", "7z", "python", "java", "node", "cmd", "powershell",
            "cefsubprocess", "shadercompile", "unrealpak", "handler"
        };

        static readonly string[] SkipDirs =
        {
            "_commonredist", "commonredist", "redist", "redistributables", "directx", "support", "prerequisites", "__installer",
            "content", "paks", "movies", "localization", "easyanticheat", "battleye", "extras", "_redist", "dotnet", "vcredist",
            "mono", "monobleedingedge", "plugins", "data", "logs", "saves", "screenshots", "shadercache"
        };

        static readonly string[] SourceTools = { "vbsp", "vrad", "vvis", "hammer", "hlmv", "studiomdl", "height2normal", "vtex", "captioncompiler", "glview", "hammerplusplus" };

        static readonly string[] SkipSteamNames =
        {
            "steamworks", "redistributable", "proton", "steam linux runtime", "steamvr", "wallpaper engine", "soundtrack",
            "dedicated server", "sdk", "benchmark", "spacewar"
        };

        public static List<FoundGame> Scan()
        {
            List<FoundGame> res = new List<FoundGame>();
            HashSet<string> seenExe = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            Action<string, string> add = (name, exe) =>
            {
                if (string.IsNullOrEmpty(exe)) return;
                string key = Game.Strip(exe.Split(';')[0]);
                if (key.Length == 0 || !seenExe.Add(key)) return;
                res.Add(new FoundGame { Name = name.Trim(), Exe = exe });
            };

            try { ScanSteam(add); } catch { }
            try { ScanEpic(add); } catch { }
            try { ScanRiot(add); } catch { }
            try { ScanUninstall(add); } catch { }
            return res;
        }

        static string[] Known(string name)
        {
            foreach (string[] k in KnownGames)
                if (name.IndexOf(k[0], StringComparison.OrdinalIgnoreCase) >= 0) return k;
            return null;
        }

        // ---- Steam
        static void ScanSteam(Action<string, string> add)
        {
            string steam = null;
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                if (k != null) steam = k.GetValue("SteamPath") as string;
            if (steam == null)
                using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\WOW6432Node\Valve\Steam"))
                    if (k != null) steam = k.GetValue("InstallPath") as string;
            if (steam == null) return;
            steam = steam.Replace('/', '\\');

            List<string> libs = new List<string> { steam };
            string vdf = Path.Combine(steam, @"steamapps\libraryfolders.vdf");
            if (File.Exists(vdf))
            {
                string txt = File.ReadAllText(vdf);
                foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(txt, "\"(?:path|\\d+)\"\\s+\"([A-Za-z]:[^\"]*)\""))
                {
                    string p = m.Groups[1].Value.Replace("\\\\", "\\");
                    if (!libs.Any(x => string.Equals(x.TrimEnd('\\'), p.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))) libs.Add(p);
                }
            }

            foreach (string lib in libs)
            {
                string apps = Path.Combine(lib, "steamapps");
                if (!Directory.Exists(apps)) continue;
                foreach (string acf in Directory.GetFiles(apps, "appmanifest_*.acf"))
                {
                    string t = File.ReadAllText(acf);
                    string name = Vdf(t, "name"), dir = Vdf(t, "installdir"), appid = Vdf(t, "appid");
                    if (name == null || dir == null || appid == "228980") continue;
                    if (SkipSteamNames.Any(s => name.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)) continue;
                    string folder = Path.Combine(apps, "common", dir);
                    if (!Directory.Exists(folder)) continue;
                    string[] known = Known(name) ?? Known(dir);
                    add(known != null ? known[1] : name, known != null ? known[2] : FindMainExe(folder, name));
                }
            }
        }

        static string Vdf(string txt, string key)
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(txt, "\"" + key + "\"\\s+\"([^\"]*)\"", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }

        // ---- Epic Games
        static void ScanEpic(Action<string, string> add)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), @"Epic\EpicGamesLauncher\Data\Manifests");
            if (!Directory.Exists(dir)) return;
            foreach (string f in Directory.GetFiles(dir, "*.item"))
            {
                string t = File.ReadAllText(f);
                string name = Json(t, "DisplayName"), loc = Json(t, "InstallLocation"), launch = Json(t, "LaunchExecutable");
                if (name == null || loc == null) continue;
                if (t.IndexOf("\"games\"", StringComparison.OrdinalIgnoreCase) < 0 && t.IndexOf("AppCategories", StringComparison.Ordinal) >= 0) continue;
                if (name.IndexOf("Unreal Engine", StringComparison.OrdinalIgnoreCase) >= 0) continue;
                string[] known = Known(name);
                if (known != null) { add(known[1], known[2]); continue; }
                if (!Directory.Exists(loc)) continue;
                string exe = FindMainExe(loc, name);
                if (exe == null && !string.IsNullOrEmpty(launch)) exe = Path.GetFileName(launch);
                add(name, exe);
            }
        }

        static string Json(string txt, string key)
        {
            System.Text.RegularExpressions.Match m = System.Text.RegularExpressions.Regex.Match(txt, "\"" + key + "\"\\s*:\\s*\"((?:[^\"\\\\]|\\\\.)*)\"");
            return m.Success ? m.Groups[1].Value.Replace("\\\\", "\\").Replace("\\/", "/") : null;
        }

        // ---- Riot (Valorant / LoL)
        static void ScanRiot(Action<string, string> add)
        {
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                if (d.DriveType != DriveType.Fixed || !d.IsReady) continue;
                string root = Path.Combine(d.RootDirectory.FullName, "Riot Games");
                if (Directory.Exists(Path.Combine(root, "VALORANT"))) add("Valorant", "VALORANT-Win64-Shipping.exe");
                if (Directory.Exists(Path.Combine(root, "League of Legends"))) add("League of Legends", "League of Legends.exe");
            }
        }

        // ---- Battle.net / Ubisoft / EA / autres : jeux connus presents dans "Applications installees"
        static void ScanUninstall(Action<string, string> add)
        {
            string[] roots = { @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall" };
            foreach (RegistryKey hive in new[] { Registry.LocalMachine, Registry.CurrentUser })
                foreach (string r in roots)
                    using (RegistryKey k = hive.OpenSubKey(r))
                    {
                        if (k == null) continue;
                        foreach (string sub in k.GetSubKeyNames())
                            using (RegistryKey s = k.OpenSubKey(sub))
                            {
                                string name = s == null ? null : s.GetValue("DisplayName") as string;
                                if (string.IsNullOrEmpty(name)) continue;
                                string[] known = Known(name);
                                if (known != null) add(known[1], known[2]);
                            }
                    }
        }

        // ---- trouve l'exe principal d'un dossier de jeu
        static string Norm(string s)
        {
            StringBuilder b = new StringBuilder();
            foreach (char c in s.ToLowerInvariant()) if (char.IsLetterOrDigit(c)) b.Append(c);
            return b.ToString();
        }

        static bool LooksLike(string exeName, string gameName)
        {
            string e = Norm(Path.GetFileNameWithoutExtension(exeName)), g = Norm(gameName);
            if (e.Length < 3 || g.Length < 3) return false;
            int n = Math.Min(5, Math.Min(e.Length, g.Length));
            return e.StartsWith(g.Substring(0, n)) || g.StartsWith(e.Substring(0, n)) || g.Contains(e) || e.Contains(g);
        }

        public static string FindMainExe(string folder, string gameName)
        {
            List<FileInfo> exes = new List<FileInfo>();
            Collect(new DirectoryInfo(folder), 0, exes);
            string folderName = Path.GetFileName(folder.TrimEnd('\\'));
            List<FileInfo> good = exes.Where(f => !BadExe.Any(b => f.Name.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0)).ToList();
            // 1) un exe qui porte le nom du jeu
            FileInfo named = good.Where(f => LooksLike(f.Name, gameName) || LooksLike(f.Name, folderName))
                                 .OrderByDescending(f => f.Name.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase))
                                 .ThenByDescending(f => f.Length).FirstOrDefault();
            if (named != null && !named.Name.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase))
            {
                // si un "...-Shipping.exe" existe, c'est lui le vrai jeu (Unreal)
                FileInfo s = good.FirstOrDefault(f => f.Name.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase));
                if (s != null) return s.Name;
            }
            // jeux Unreal avec petit lanceur a la racine (ex : SeaOfThieves.exe -> Athena\Binaries\Win64\SoTGame.exe)
            FileInfo bin = good.Where(f => f.FullName.IndexOf("\\Binaries\\", StringComparison.OrdinalIgnoreCase) >= 0)
                               .OrderByDescending(f => f.Length).FirstOrDefault();
            if (named != null && bin != null && bin.Length > named.Length * 5) return bin.Name;
            if (named != null) return named.Name;
            // jeux Unreal : le vrai jeu est "...-Win64-Shipping.exe"
            FileInfo ship = exes.Where(f => f.Name.EndsWith("-Shipping.exe", StringComparison.OrdinalIgnoreCase)
                                         && f.Name.IndexOf("_BE", StringComparison.OrdinalIgnoreCase) < 0
                                         && f.Name.IndexOf("_EAC", StringComparison.OrdinalIgnoreCase) < 0)
                                .OrderByDescending(f => f.Length).FirstOrDefault();
            if (ship != null) return ship.Name;
            FileInfo best = good.Where(f => f.Name.IndexOf("launcher", StringComparison.OrdinalIgnoreCase) < 0
                                         && f.Name.IndexOf("handler", StringComparison.OrdinalIgnoreCase) < 0
                                         && !SourceTools.Contains(Path.GetFileNameWithoutExtension(f.Name).ToLowerInvariant()))
                                .OrderByDescending(f => f.Length).FirstOrDefault()
                             ?? good.OrderByDescending(f => f.Length).FirstOrDefault();
            return best != null ? best.Name : null;
        }

        static void Collect(DirectoryInfo dir, int depth, List<FileInfo> exes)
        {
            if (depth > 4 || exes.Count > 400) return;
            try
            {
                foreach (FileInfo f in dir.GetFiles("*.exe")) exes.Add(f);
                foreach (DirectoryInfo d in dir.GetDirectories())
                {
                    string n = d.Name.ToLowerInvariant();
                    if (SkipDirs.Contains(n) || (d.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                    Collect(d, depth + 1, exes);
                }
            }
            catch { }
        }
    }

    static class Startup
    {
        const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";
        const string Name = "StretchRes";

        public static bool IsEnabled()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(Key))
                return k != null && k.GetValue(Name) != null;
        }

        public static void Set(bool on, string exePath)
        {
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(Key))
            {
                if (on) k.SetValue(Name, "\"" + exePath + "\" --tray");
                else k.DeleteValue(Name, false);
            }
        }
    }

    // ------------------------------------------------------------------ Theme
    static class Theme
    {
        public static readonly Color Bg = Color.FromArgb(17, 17, 23);
        public static readonly Color Panel = Color.FromArgb(27, 27, 35);
        public static readonly Color Panel2 = Color.FromArgb(40, 40, 52);
        public static readonly Color Border = Color.FromArgb(55, 55, 70);
        public static readonly Color Text = Color.FromArgb(236, 236, 242);
        public static readonly Color Dim = Color.FromArgb(145, 145, 162);
        public static readonly Color Accent = Color.FromArgb(255, 138, 0);
        public static readonly Color Green = Color.FromArgb(61, 220, 132);
        public static readonly Color Gold = Color.FromArgb(255, 205, 60);

        static Icon icon;
        public static Icon AppIcon
        {
            get
            {
                if (icon != null) return icon;
                Bitmap bmp = new Bitmap(32, 32);
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.Clear(Color.Transparent);
                    using (GraphicsPath p = Rounded(new Rectangle(1, 5, 30, 22), 5))
                    using (SolidBrush b = new SolidBrush(Accent)) g.FillPath(b, p);
                    using (Pen pen = new Pen(Color.FromArgb(20, 20, 26), 3f))
                    {
                        pen.StartCap = LineCap.ArrowAnchor; pen.EndCap = LineCap.ArrowAnchor;
                        g.DrawLine(pen, 7, 16, 25, 16);
                    }
                }
                icon = Icon.FromHandle(bmp.GetHicon());
                return icon;
            }
        }

        public static GraphicsPath Rounded(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            int d = rad * 2;
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        // Polices partagees : une seule instance par (police, taille, style), jamais recreee.
        // Evite d'empiler des polices a chaque reconstruction de la fenetre (changement de langue).
        static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();
        public static Font GetFont(string name, float size, FontStyle style)
        {
            string key = name + "|" + size.ToString(System.Globalization.CultureInfo.InvariantCulture) + "|" + (int)style;
            Font f;
            if (!fonts.TryGetValue(key, out f)) { f = new Font(name, size, style); fonts[key] = f; }
            return f;
        }
        public static Font UI(float size, FontStyle style = FontStyle.Regular) { return GetFont(L.FontName, size, style); }

        public static Button MakeButton(string text, bool accent)
        {
            Button b = new Button();
            b.Text = text;
            b.FlatStyle = FlatStyle.Flat;
            b.Cursor = Cursors.Hand;
            b.BackColor = accent ? Accent : Panel2;
            b.ForeColor = accent ? Color.FromArgb(20, 20, 26) : Text;
            b.FlatAppearance.BorderColor = accent ? Accent : Border;
            b.FlatAppearance.MouseOverBackColor = accent ? Color.FromArgb(255, 160, 50) : Color.FromArgb(52, 52, 66);
            b.Font = Theme.UI(9.5f, accent ? FontStyle.Bold : FontStyle.Regular);
            return b;
        }

        public static Label MakeLabel(string text, int x, int y, Color c, float size, FontStyle st)
        {
            Label l = new Label();
            l.Text = text; l.AutoSize = true; l.Location = new Point(x, y);
            l.ForeColor = c; l.BackColor = Color.Transparent;
            l.Font = Theme.UI(size, st);
            return l;
        }
    }

    class FormatItem
    {
        public int W, H; // W=0 -> natif ; W=-1 -> "autre"
        public bool Fav;
        public string Text;
        public override string ToString() { return Text; }
    }

    // ------------------------------------------------------------------ App (tray)
    class App : ApplicationContext
    {
        public Config Cfg;
        public HashSet<string> Running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        public bool Exiting;
        NotifyIcon tray;
        MainForm form;
        System.Windows.Forms.Timer timer;
        ToolStripMenuItem miDetect;

        string appliedName; int appliedW, appliedH; bool appliedStretch;
        string failedKey;
        DateTime testUntil = DateTime.MinValue;
        bool closeHintShown;

        public MainForm Form { get { return form; } }

        // Mode capture d'ecran uniquement : pas d'icone, pas de surveillance, rien n'est modifie.
        public App(Config sample)
        {
            Cfg = sample;
            form = new MainForm(this);
            Running.Add("FortniteClient-Win64-Shipping");
        }

        public App(bool show, bool firstRun)
        {
            Cfg = Config.Load();
            if (!L.Set(Cfg.Lang))
            {
                // premier lancement (ou mise a jour depuis une version sans langues) : on demande.
                // Au demarrage discret de Windows on ne derange pas : langue de Windows en attendant.
                if (show) { Cfg.Lang = LangPicker.Ask(L.Detect()); Cfg.Save(); L.Set(Cfg.Lang); }
                else L.Set(L.Detect());
            }
            form = new MainForm(this);
            IntPtr h = form.Handle; // force la creation pour BeginInvoke

            tray = new NotifyIcon();
            tray.Icon = Theme.AppIcon;
            tray.Text = "StretchRes";
            tray.ContextMenuStrip = BuildMenu();
            tray.Visible = true;
            tray.MouseClick += (s, e) => { if (e.Button == MouseButtons.Left) ShowForm(); };
            tray.BalloonTipClicked += (s, e) => ShowForm();

            timer = new System.Windows.Forms.Timer();
            timer.Interval = 500;
            timer.Tick += (s, e) => Tick();
            timer.Start();

            Application.ApplicationExit += (s, e) => Display.Restore();
            SystemEvents.SessionEnding += (s, e) => Display.Restore();
            StartShowListener();

            if (firstRun)
                tray.ShowBalloonTip(6000, L.T("installed_title"), L.T("installed_body"), ToolTipIcon.Info);
            if (show) ShowForm();
            Tick();
            // ajoute tout seul les nouveaux jeux installes : tout de suite si la fenetre est ouverte,
            // sinon 45 s apres le demarrage de Windows pour ne pas le ralentir.
            if (show) ScanGames(false);
            else
            {
                System.Windows.Forms.Timer once = new System.Windows.Forms.Timer();
                once.Interval = 45000;
                once.Tick += (s, e) => { once.Stop(); once.Dispose(); ScanGames(false); };
                once.Start();
            }
        }

        ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            ToolStripMenuItem open = new ToolStripMenuItem(L.T("menu_open"), null, (s, e) => ShowForm());
            open.Font = Theme.GetFont(open.Font.Name, open.Font.Size, FontStyle.Bold);
            m.Items.Add(open);
            m.Items.Add(new ToolStripSeparator());
            miDetect = new ToolStripMenuItem(L.T("menu_detect"), null, (s, e) => SetDetection(!Cfg.Detection));
            miDetect.Checked = Cfg.Detection;
            m.Items.Add(miDetect);
            m.Items.Add(new ToolStripMenuItem(L.T("menu_restore"), null, (s, e) => ForceRestore()));
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem(L.T("menu_quit"), null, (s, e) => Quit()));
            return m;
        }

        // Change la langue tout de suite : la fenetre et le menu de l'icone sont reconstruits.
        public void SetLanguage(string code)
        {
            if (code == L.Code || !L.Set(code)) return;
            Cfg.Lang = code; Cfg.Save();
            MainForm old = form;
            old.BeginInvoke(new Action(() =>
            {
                Point loc = old.Location;
                form = new MainForm(this);
                IntPtr h = form.Handle;
                form.StartPosition = FormStartPosition.Manual;
                form.Location = loc;
                form.SetScanning(scanning);
                old.Discard = true;
                old.Close();
                ContextMenuStrip oldMenu = tray.ContextMenuStrip;
                tray.ContextMenuStrip = BuildMenu();
                if (oldMenu != null) oldMenu.Dispose();
                lastTip = null;
                ShowForm();
                Tick();
            }));
        }

        void StartShowListener()
        {
            EventWaitHandle ev = new EventWaitHandle(false, EventResetMode.AutoReset, Program.ShowEventName);
            Thread t = new Thread(() =>
            {
                while (true)
                {
                    ev.WaitOne();
                    try { form.BeginInvoke(new Action(ShowForm)); } catch { }
                }
            });
            t.IsBackground = true;
            t.Start();

            // permet a une nouvelle version de demander a celle-ci de se fermer proprement (mise a jour)
            EventWaitHandle quit = new EventWaitHandle(false, EventResetMode.AutoReset, Program.QuitEventName);
            Thread t2 = new Thread(() =>
            {
                quit.WaitOne();
                try { form.BeginInvoke(new Action(Quit)); } catch { }
            });
            t2.IsBackground = true;
            t2.Start();
        }

        public void ShowForm()
        {
            form.Show();
            if (form.WindowState == FormWindowState.Minimized) form.WindowState = FormWindowState.Normal;
            form.Activate();
            form.RefreshAll();
            form.FocusSearch(); // on peut taper le nom du jeu directement
        }

        public void FormHidden()
        {
            Native.TrimMemory();
            if (!closeHintShown && !Cfg.HintShown)
            {
                closeHintShown = true; Cfg.HintShown = true; Cfg.Save();
                tray.ShowBalloonTip(4000, L.T("bg_title"), L.T("bg_body"), ToolTipIcon.Info);
            }
        }

        public void SetDetection(bool on)
        {
            Cfg.Detection = on; Cfg.Save();
            if (miDetect == null) return;
            miDetect.Checked = on;
            form.RefreshAll();
            Tick();
        }

        public bool IsRunning(Game g)
        {
            if (g == lastFocused && trackedHandle != IntPtr.Zero) return true;
            foreach (string p in g.ProcNames) if (Running.Contains(p)) return true;
            return false;
        }
        public bool Testing { get { return testUntil > DateTime.Now; } }

        public void ForceRestore()
        {
            testUntil = DateTime.MinValue;
            if (Display.IsChanged || !Display.AtNative()) Display.ForceReset();
            appliedName = null;
            failedKey = null;
            Tick();
        }

        public void StartTest(Game g)
        {
            if (g.W <= 0) { MessageBox.Show(form, L.T("test_native"), "StretchRes"); return; }
            string err = Display.Apply(g.W, g.H, Cfg.Stretch);
            if (err != null) { MessageBox.Show(form, err, "StretchRes", MessageBoxButtons.OK, MessageBoxIcon.Warning); return; }
            appliedName = "__test__"; appliedW = g.W; appliedH = g.H; appliedStretch = Cfg.Stretch;
            testUntil = DateTime.Now.AddSeconds(10);
            Tick();
        }

        public void StopTest()
        {
            testUntil = DateTime.MinValue;
            Display.Restore();
            appliedName = null;
            Tick();
        }

        int tickCount;
        DateTime lastGameFocus = DateTime.MinValue;
        Game lastFocused;
        bool lastBackground, needWatch = true, scanning;
        string lastTip;
        uint fgPid, fgGamePid, trackedPid; string fgName;
        Game fgGameCached;
        IntPtr trackedHandle = IntPtr.Zero;

        // Nom du programme au premier plan (mis en cache tant que la fenetre active ne change pas de processus).
        string ForegroundProcess(out uint pid)
        {
            pid = 0;
            IntPtr h = Native.GetForegroundWindow();
            if (h == IntPtr.Zero) return null;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid == 0) return null;
            if (pid != fgPid) { fgPid = pid; fgName = Native.ProcessNameOf(pid); }
            return fgName;
        }

        // Garde un oeil sur le processus du jeu pour savoir quand il se ferme.
        void Track(Game g, uint pid)
        {
            if (trackedHandle != IntPtr.Zero) { Native.CloseHandle(trackedHandle); trackedHandle = IntPtr.Zero; }
            lastFocused = g; trackedPid = pid;
            if (g != null && pid != 0) trackedHandle = Native.OpenProcess(0x00100000 | 0x1000 /* SYNCHRONIZE | QUERY_LIMITED */, false, pid);
        }

        bool TrackedAlive()
        {
            if (trackedHandle == IntPtr.Zero) return false;
            return Native.WaitForSingleObject(trackedHandle, 0) != 0; // 0 = le processus est termine
        }

        // Cherche les jeux installes en arriere-plan puis ajoute les nouveaux (format natif par defaut).
        public void ScanGames(bool userAsked)
        {
            if (scanning || form == null) return;
            scanning = true;
            form.SetScanning(true);
            Thread t = new Thread(() =>
            {
                List<FoundGame> found = new List<FoundGame>();
                try { found = Scanner.Scan(); } catch { }
                try { form.BeginInvoke(new Action(() => ScanDone(found, userAsked))); } catch { }
            });
            t.IsBackground = true;
            t.Priority = ThreadPriority.BelowNormal; // ne gene jamais un jeu en cours
            t.Start();
        }

        void ScanDone(List<FoundGame> found, bool userAsked)
        {
            scanning = false;
            int added = 0;
            List<string> names = new List<string>();
            foreach (FoundGame f in found)
            {
                if (Cfg.Known(f.Exe)) continue;
                Cfg.Games.Add(new Game { Name = f.Name, Exe = f.Exe, W = 0, H = 0, Enabled = true });
                names.Add(f.Name);
                added++;
            }
            Cfg.Scanned = true;
            Cfg.SortGames();
            Cfg.Save();
            form.SetScanning(false);
            form.RefreshAll();
            if (added > 0)
                tray.ShowBalloonTip(5000, L.F("scan_added", added),
                    string.Join(", ", names.Take(6)) + (names.Count > 6 ? "…" : "") + "\n" + L.T("scan_added_body"), ToolTipIcon.Info);
            else if (userAsked)
                MessageBox.Show(form, L.T("scan_none"), "StretchRes");
            if (!form.Visible) Native.TrimMemory();
        }

        void Tick()
        {
            tickCount++;
            // La liste complete des processus ne sert qu'a l'affichage "EN JEU" : seulement fenetre ouverte, toutes les 2 s.
            if (form.Visible) { if (tickCount % 4 == 1) Running = Native.ProcessNames(); }
            else if (Running.Count > 0) Running = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (tickCount % 4 == 0) needWatch = Cfg.Detection && Cfg.Games.Any(g => g.Enabled && g.W > 0);

            if (appliedName == "__test__")
            {
                int left = (int)Math.Ceiling((testUntil - DateTime.Now).TotalSeconds);
                if (left > 0) { form.SetTestCountdown(left); form.RefreshLive(L.F("st_test", appliedW, appliedH), true); return; }
                Display.Restore(); appliedName = null; testUntil = DateTime.MinValue;
                form.SetTestCountdown(0);
            }

            // Quel jeu commande l'ecran ? Celui qui est AU PREMIER PLAN, avec SA resolution a lui.
            // (Fortnite ouvert en fond ne doit jamais imposer sa resolution a un autre jeu.)
            // Detection tres legere : on regarde seulement la fenetre au premier plan, et on suit le jeu
            // via un "handle" qui nous previent quand il se ferme. Aucune liste de processus en tache de fond.
            Game target = null, fgGame = null;
            bool background = false;
            if (needWatch || appliedName != null)
            {
                uint pid;
                string fg = ForegroundProcess(out pid);
                if (fg != null && Cfg.Detection)
                {
                    if (pid != fgGamePid || tickCount % 4 == 0) // recalcule seulement si la fenetre active change
                    {
                        fgGamePid = pid;
                        fgGameCached = Cfg.Games.FirstOrDefault(g => g.Enabled && g.Matches(fg));
                    }
                    fgGame = fgGameCached;
                }
                if (fgGame != null)
                {
                    lastGameFocus = DateTime.Now;
                    if (lastFocused != fgGame || trackedPid != pid) Track(fgGame, pid);
                }
                if (lastFocused != null && (!lastFocused.Enabled || !Cfg.Games.Contains(lastFocused) || !TrackedAlive()))
                    Track(null, 0);
                target = fgGame ?? lastFocused;
                if (target != null && fgGame == null)
                    background = fg == null ? lastBackground
                               : Cfg.NormalOnAltTab && (DateTime.Now - lastGameFocus).TotalMilliseconds > 700;
            }
            else Track(null, 0);
            lastBackground = background;

            string status; bool active = false;
            if (target != null && (background || target.W <= 0))
            {
                if (appliedName != null) { Display.Restore(); appliedName = null; }
                status = background ? L.F("st_bg", target.Name) : L.F("st_native", target.Name);
                active = !background;
            }
            else if (target != null)
            {
                bool same = appliedName != null && appliedW == target.W && appliedH == target.H && appliedStretch == Cfg.Stretch;
                string key = target.Exe + target.W + "x" + target.H + Cfg.Stretch;
                if (same)
                {
                    appliedName = target.Name;
                    status = target.Name + " → " + target.W + " × " + target.H; active = true;
                }
                else if (failedKey == key)
                {
                    status = L.F("st_refused", target.Name);
                }
                else
                {
                    string err = Display.Apply(target.W, target.H, Cfg.Stretch);
                    if (err == null)
                    {
                        appliedName = target.Name; appliedW = target.W; appliedH = target.H; appliedStretch = Cfg.Stretch;
                        failedKey = null;
                        status = target.Name + " → " + target.W + " × " + target.H; active = true;
                    }
                    else
                    {
                        failedKey = key;
                        tray.ShowBalloonTip(6000, "StretchRes — " + target.Name, err, ToolTipIcon.Warning);
                        status = L.F("st_refused", target.Name);
                    }
                }
            }
            else
            {
                failedKey = null;
                if (appliedName != null) { Display.Restore(); appliedName = null; }
                status = Cfg.Detection ? L.T("st_waiting") : L.T("st_off");
            }

            string tip = "StretchRes — " + status;
            if (tip.Length > 63) tip = tip.Substring(0, 63);
            if (tip != lastTip) { tray.Text = tip; lastTip = tip; }
            if (form.Visible) form.RefreshLive(status, active);
        }

        public void Quit()
        {
            Exiting = true;
            timer.Stop();
            Display.Restore();
            tray.Visible = false;
            tray.Dispose();
            form.Close();
            ExitThread();
        }

        public void Uninstall()
        {
            Exiting = true;
            timer.Stop();
            Display.Restore();
            try { Startup.Set(false, Program.ExePath); } catch { }
            tray.Visible = false;
            tray.Dispose();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("cmd.exe",
                    "/c ping 127.0.0.1 -n 3 > nul & rmdir /s /q \"" + Config.Dir + "\"");
                psi.CreateNoWindow = true; psi.WindowStyle = ProcessWindowStyle.Hidden; psi.UseShellExecute = false;
                Process.Start(psi);
            }
            catch { }
            form.Close();
            ExitThread();
        }
    }

    // ------------------------------------------------------------------ Fenetre
    class MainForm : Form
    {
        App app;
        Config cfg { get { return app.Cfg; } }
        List<Mode> modes = new List<Mode>();
        Size native;

        Label lblStatus, lblEmpty, lblExe, lblGames, btnClear;
        TextBox txtSearch;
        ListBox list;
        TextBox txtName;
        ComboBox cmbFormat;
        CheckBox chkEnabled, chkStartup, chkStretch, chkAltTab;
        Button btnAdd, btnScan, btnRemove, btnTest, btnFav;
        ComboBox cmbLang;
        public bool Discard; // fenetre remplacee (changement de langue) : fermeture reelle
        ToolTip tip = new ToolTip();
        Panel right;
        bool loading;
        Font fBold = Theme.UI(10.5f, FontStyle.Bold);
        Font fSmall = Theme.UI(8.5f);
        Font fPill = Theme.UI(7.5f, FontStyle.Bold);

        static readonly string[][] Popular =
        {
            new[] { "Fortnite", "FortniteClient-Win64-Shipping.exe" },
            new[] { "Counter-Strike 2", "cs2.exe" },
            new[] { "@csgo_old", "csgo.exe" }, // "@cle" = nom traduit
            new[] { "Valorant", "VALORANT-Win64-Shipping.exe" },
            new[] { "Apex Legends", "r5apex.exe" },
            new[] { "Apex Legends (DX12)", "r5apex_dx12.exe" },
            new[] { "Rocket League", "RocketLeague.exe" },
            new[] { "Rainbow Six Siege", "RainbowSix.exe" },
            new[] { "Overwatch 2", "Overwatch.exe" },
            new[] { "PUBG", "TslGame.exe" },
        };

        public MainForm(App app)
        {
            this.app = app;
            Text = L.T("title");
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            ClientSize = new Size(760, 540);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.UI(9.5f);
            StartPosition = FormStartPosition.CenterScreen;
            Icon = Theme.AppIcon;
            Build();
        }

        void Build()
        {
            Controls.Add(Theme.MakeLabel("StretchRes", 20, 14, Theme.Text, 20f, FontStyle.Bold));
            Label subtitle = Theme.MakeLabel(L.T("subtitle"), 23, 56, Theme.Dim, 9f, FontStyle.Regular);
            subtitle.AutoSize = false; subtitle.Size = new Size(585, 22); subtitle.AutoEllipsis = true;
            Controls.Add(subtitle);

            lblStatus = new Label();
            lblStatus.AutoSize = false; lblStatus.Size = new Size(370, 26); lblStatus.Location = new Point(370, 18);
            lblStatus.TextAlign = ContentAlignment.MiddleRight; lblStatus.AutoEllipsis = true;
            lblStatus.Font = Theme.UI(9.5f, FontStyle.Bold);
            Controls.Add(lblStatus);

            // --- langue (modifiable a tout moment)
            cmbLang = new ComboBox();
            cmbLang.DropDownStyle = ComboBoxStyle.DropDownList; cmbLang.FlatStyle = FlatStyle.Flat;
            cmbLang.BackColor = Theme.Panel2; cmbLang.ForeColor = Theme.Text;
            cmbLang.DrawMode = DrawMode.OwnerDrawFixed; cmbLang.ItemHeight = 20;
            cmbLang.DrawItem += (s, e) =>
            {
                if (e.Index < 0) return;
                bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
                using (SolidBrush b = new SolidBrush(sel ? Color.FromArgb(70, 50, 20) : Theme.Panel2)) e.Graphics.FillRectangle(b, e.Bounds);
                TextRenderer.DrawText(e.Graphics, L.Names[e.Index], cmbLang.Font,
                    new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height), Theme.Text,
                    TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            };
            cmbLang.Items.AddRange(L.Names);
            cmbLang.SelectedIndex = L.Index;
            cmbLang.Location = new Point(620, 52); cmbLang.Width = 120;
            cmbLang.SelectionChangeCommitted += (s, e) => app.SetLanguage(L.Codes[cmbLang.SelectedIndex]);
            tip.SetToolTip(cmbLang, L.T("lang"));
            Controls.Add(cmbLang);

            Controls.Add(Line(20, 86, 720));

            // --- liste des jeux
            lblGames = Theme.MakeLabel(L.T("my_games"), 20, 102, Theme.Dim, 8.5f, FontStyle.Bold);
            Controls.Add(lblGames);

            // --- recherche
            Panel searchBox = new Panel();
            searchBox.Location = new Point(20, 124); searchBox.Size = new Size(334, 30); searchBox.BackColor = Theme.Panel2;
            Controls.Add(searchBox);
            searchBox.Controls.Add(Theme.MakeLabel("⌕", 6, 3, Theme.Dim, 12f, FontStyle.Regular));
            txtSearch = new TextBox();
            txtSearch.BorderStyle = BorderStyle.None; txtSearch.BackColor = Theme.Panel2; txtSearch.ForeColor = Theme.Text;
            txtSearch.Font = Theme.UI(10f);
            txtSearch.Location = new Point(28, 6); txtSearch.Width = 278;
            txtSearch.TextChanged += (s, e) => { btnClear.Visible = txtSearch.Text.Length > 0; RebuildList(Selected()); };
            txtSearch.KeyDown += (s, e) =>
            {
                if (e.KeyCode == Keys.Escape) { txtSearch.Clear(); e.SuppressKeyPress = true; }
                else if (e.KeyCode == Keys.Down || e.KeyCode == Keys.Enter)
                {
                    if (view.Count > 0) { list.Focus(); if (list.SelectedIndex < 0) list.SelectedIndex = 0; }
                    e.SuppressKeyPress = true;
                }
            };
            txtSearch.HandleCreated += (s, e) => Native.SendMessage(txtSearch.Handle, 0x1501, (IntPtr)1, L.T("search"));
            searchBox.Controls.Add(txtSearch);
            btnClear = Theme.MakeLabel("✕", 310, 5, Theme.Dim, 10f, FontStyle.Regular);
            btnClear.Cursor = Cursors.Hand; btnClear.Visible = false;
            btnClear.Click += (s, e) => { txtSearch.Clear(); txtSearch.Focus(); };
            searchBox.Controls.Add(btnClear);
            searchBox.Click += (s, e) => txtSearch.Focus();

            list = new ListBox();
            list.Location = new Point(20, 160); list.Size = new Size(334, 266);
            list.BorderStyle = BorderStyle.None; list.BackColor = Theme.Panel; list.ForeColor = Theme.Text;
            list.DrawMode = DrawMode.OwnerDrawFixed; list.ItemHeight = 52; list.IntegralHeight = false;
            list.DrawItem += DrawGame;
            list.SelectedIndexChanged += (s, e) => LoadSelected();
            Controls.Add(list);

            lblEmpty = Theme.MakeLabel(L.T("empty"), 0, 0, Theme.Dim, 9.5f, FontStyle.Regular);
            lblEmpty.AutoSize = false; lblEmpty.Size = new Size(334, 60); lblEmpty.TextAlign = ContentAlignment.MiddleCenter;
            lblEmpty.Location = new Point(20, 250); lblEmpty.BackColor = Theme.Panel;
            Controls.Add(lblEmpty);
            lblEmpty.BringToFront();

            // largeur des 3 boutons selon la longueur des textes dans la langue choisie (334 px au total)
            btnScan = Theme.MakeButton(L.T("scan"), true);
            btnAdd = Theme.MakeButton(L.T("add"), false);
            btnRemove = Theme.MakeButton(L.T("remove"), false);
            int wAdd = Math.Max(78, TextRenderer.MeasureText(btnAdd.Text, btnAdd.Font).Width + 22);
            int wRem = Math.Max(70, TextRenderer.MeasureText(btnRemove.Text, btnRemove.Font).Width + 22);
            int wScan = 334 - wAdd - wRem - 12;

            btnScan.Location = new Point(20, 436); btnScan.Size = new Size(wScan, 36);
            btnScan.Click += (s, e) => app.ScanGames(true);
            Controls.Add(btnScan);

            btnAdd.Location = new Point(20 + wScan + 6, 436); btnAdd.Size = new Size(wAdd, 36);
            btnAdd.Click += (s, e) =>
            {
                ContextMenuStrip m = BuildAddMenu();
                m.Closed += (s2, e2) => BeginInvoke(new Action(m.Dispose)); // libere le menu apres usage
                m.Show(btnAdd, new Point(0, btnAdd.Height));
            };
            Controls.Add(btnAdd);

            btnRemove.Location = new Point(354 - wRem, 436); btnRemove.Size = new Size(wRem, 36);
            btnRemove.Click += (s, e) => RemoveSelected();
            Controls.Add(btnRemove);

            // --- reglages du jeu
            right = new Panel();
            right.Location = new Point(372, 102); right.Size = new Size(368, 370); right.BackColor = Theme.Panel;
            Controls.Add(right);

            right.Controls.Add(Theme.MakeLabel(L.T("settings"), 16, 14, Theme.Dim, 8.5f, FontStyle.Bold));
            right.Controls.Add(Theme.MakeLabel(L.T("name"), 16, 42, Theme.Text, 9f, FontStyle.Regular));
            txtName = new TextBox();
            txtName.Location = new Point(16, 62); txtName.Size = new Size(336, 26);
            txtName.BackColor = Theme.Panel2; txtName.ForeColor = Theme.Text; txtName.BorderStyle = BorderStyle.FixedSingle;
            txtName.TextChanged += (s, e) =>
            {
                Game g = Selected(); if (loading || g == null) return;
                g.Name = txtName.Text.Trim().Length > 0 ? txtName.Text.Trim() : g.ProcName; cfg.Save(); list.Invalidate();
            };
            right.Controls.Add(txtName);

            right.Controls.Add(Theme.MakeLabel(L.T("watched"), 16, 98, Theme.Text, 9f, FontStyle.Regular));
            lblExe = Theme.MakeLabel("", 16, 118, Theme.Dim, 9f, FontStyle.Regular);
            right.Controls.Add(lblExe);

            right.Controls.Add(Theme.MakeLabel(L.T("format"), 16, 148, Theme.Text, 9f, FontStyle.Regular));
            cmbFormat = new ComboBox();
            cmbFormat.Location = new Point(16, 168); cmbFormat.Size = new Size(296, 28);
            cmbFormat.DropDownStyle = ComboBoxStyle.DropDownList; cmbFormat.FlatStyle = FlatStyle.Flat;
            cmbFormat.BackColor = Theme.Panel2; cmbFormat.ForeColor = Theme.Text;
            cmbFormat.DrawMode = DrawMode.OwnerDrawFixed; cmbFormat.ItemHeight = 24; cmbFormat.MaxDropDownItems = 14;
            cmbFormat.DrawItem += DrawFormat;
            cmbFormat.SelectionChangeCommitted += (s, e) => { FormatChosen(); UpdateFavButton(); };
            right.Controls.Add(cmbFormat);

            btnFav = Theme.MakeButton("☆", false);
            btnFav.Font = Theme.GetFont("Segoe UI Symbol", 13f, FontStyle.Regular);
            btnFav.Location = new Point(318, 167); btnFav.Size = new Size(34, 31);
            btnFav.Padding = new Padding(0); btnFav.TextAlign = ContentAlignment.MiddleCenter;
            btnFav.Click += (s, e) => ToggleFav();
            right.Controls.Add(btnFav);

            chkEnabled = new CheckBox();
            chkEnabled.Text = L.T("enabled"); chkEnabled.AutoSize = true; chkEnabled.Location = new Point(16, 212);
            chkEnabled.CheckedChanged += (s, e) =>
            {
                Game g = Selected(); if (loading || g == null) return;
                g.Enabled = chkEnabled.Checked; cfg.Save(); list.Invalidate();
            };
            right.Controls.Add(chkEnabled);

            btnTest = Theme.MakeButton(L.T("test"), false);
            btnTest.Location = new Point(16, 244); btnTest.Size = new Size(TestWidth(btnTest.Text), 34);
            btnTest.Click += (s, e) =>
            {
                Game g = Selected(); if (g == null) return;
                if (app.Testing) app.StopTest(); else app.StartTest(g);
            };
            right.Controls.Add(btnTest);

            Label hint = Theme.MakeLabel(L.T("hint"), 16, 288, Theme.Dim, 8.5f, FontStyle.Regular);
            hint.AutoSize = false; hint.Size = new Size(336, 80);
            // texte plus long dans certaines langues : on reduit un peu la police plutot que de le couper
            for (float sz = 8.5f; sz > 7f && TextRenderer.MeasureText(hint.Text, hint.Font, new Size(336, 0), TextFormatFlags.WordBreak).Height > 80; sz -= 0.25f)
                hint.Font = Theme.UI(sz - 0.25f);
            right.Controls.Add(hint);

            // --- bas : les cases se suivent selon la longueur des textes
            Controls.Add(Line(20, 488, 720));
            chkStartup = new CheckBox();
            chkStartup.Text = L.T("startup"); chkStartup.AutoSize = true; chkStartup.Location = new Point(20, 504);
            chkStartup.CheckedChanged += (s, e) =>
            {
                if (loading) return;
                try { Startup.Set(chkStartup.Checked, Program.ExePath); } catch (Exception ex) { MessageBox.Show(this, ex.Message, "StretchRes"); }
            };
            Controls.Add(chkStartup);

            chkStretch = new CheckBox();
            chkStretch.Text = L.T("stretch"); chkStretch.AutoSize = true; chkStretch.Location = new Point(20 + chkStartup.PreferredSize.Width + 18, 504);
            chkStretch.CheckedChanged += (s, e) => { if (loading) return; cfg.Stretch = chkStretch.Checked; cfg.Save(); };
            Controls.Add(chkStretch);

            chkAltTab = new CheckBox();
            chkAltTab.Text = L.T("alttab"); chkAltTab.AutoSize = true; chkAltTab.Location = new Point(chkStretch.Left + chkStretch.PreferredSize.Width + 18, 504);
            chkAltTab.CheckedChanged += (s, e) => { if (loading) return; cfg.NormalOnAltTab = chkAltTab.Checked; cfg.Save(); };
            Controls.Add(chkAltTab);

            LinkLabel lnk = new LinkLabel();
            lnk.Text = L.T("uninstall"); lnk.AutoSize = true; lnk.Location = new Point(660, 506);
            lnk.LinkColor = Theme.Dim; lnk.ActiveLinkColor = Theme.Accent; lnk.LinkBehavior = LinkBehavior.HoverUnderline;
            lnk.LinkClicked += (s, e) =>
            {
                if (MessageBox.Show(this, L.T("uninstall_q"), "StretchRes", MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    app.Uninstall();
            };
            Controls.Add(lnk);
            lnk.Left = 740 - lnk.Width; // aligne a droite une fois la police de la fenetre appliquee
        }

        static Panel Line(int x, int y, int w)
        {
            Panel p = new Panel(); p.Location = new Point(x, y); p.Size = new Size(w, 1); p.BackColor = Theme.Border; return p;
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == (Keys.Control | Keys.F)) { FocusSearch(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        public void FocusSearch()
        {
            txtSearch.Focus();
            txtSearch.SelectAll();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) tip.Dispose(); // la bulle d'aide a sa propre fenetre Windows
            base.Dispose(disposing);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!app.Exiting && !Discard && e.CloseReason == CloseReason.UserClosing)
            {
                e.Cancel = true;
                Hide();
                app.FormHidden();
                return;
            }
            base.OnFormClosing(e);
        }

        // ---------- donnees
        List<Game> view = new List<Game>(); // jeux affiches (apres filtre de recherche)

        Game Selected()
        {
            int i = list.SelectedIndex;
            return i >= 0 && i < view.Count ? view[i] : null;
        }

        static readonly System.Globalization.CompareInfo Cmp = System.Globalization.CultureInfo.InvariantCulture.CompareInfo;
        const System.Globalization.CompareOptions SearchOpts =
            System.Globalization.CompareOptions.IgnoreCase | System.Globalization.CompareOptions.IgnoreNonSpace;

        bool MatchesSearch(Game g, string q)
        {
            if (q.Length == 0) return true;
            foreach (string word in q.Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries))
                if (Cmp.IndexOf(g.Name, word, SearchOpts) < 0 && Cmp.IndexOf(g.Exe, word, SearchOpts) < 0) return false;
            return true;
        }

        // Reconstruit la liste affichee selon la recherche, en gardant le jeu selectionne si possible.
        void RebuildList(Game keep)
        {
            bool wasLoading = loading;
            loading = true;
            string q = txtSearch.Text.Trim();
            view = cfg.Games.Where(g => MatchesSearch(g, q)).ToList();
            list.BeginUpdate();
            list.Items.Clear();
            foreach (Game g in view) list.Items.Add(g.Name);
            list.EndUpdate();
            int idx = keep != null ? view.IndexOf(keep) : -1;
            if (idx < 0 && view.Count > 0) idx = 0;
            list.SelectedIndex = idx;
            lblGames.Text = L.T("my_games") + (q.Length > 0 ? "  (" + view.Count + " / " + cfg.Games.Count + ")" : "  (" + cfg.Games.Count + ")");
            lblEmpty.Text = cfg.Games.Count == 0 ? L.T("empty") : L.F("no_match", q);
            lastLiveSig = null;
            loading = wasLoading;
            LoadSelected();
        }

        public void RefreshAll()
        {
            loading = true;
            native = Display.NativeSize();
            modes = Display.Modes();
            RebuildList(Selected());
            loading = true;
            chkStartup.Checked = Startup.IsEnabled();
            chkStretch.Checked = cfg.Stretch;
            chkAltTab.Checked = cfg.NormalOnAltTab;
            loading = false;
            LoadSelected();
        }

        void LoadSelected()
        {
            Game g = Selected();
            lblEmpty.Visible = view.Count == 0;
            right.Enabled = g != null;
            btnRemove.Enabled = g != null;
            bool wasLoading = loading;
            loading = true;
            if (g == null)
            {
                txtName.Text = ""; lblExe.Text = "—"; cmbFormat.Items.Clear(); chkEnabled.Checked = false;
            }
            else
            {
                txtName.Text = g.Name;
                lblExe.Text = g.Exe;
                chkEnabled.Checked = g.Enabled;
                FillFormats(g);
            }
            loading = wasLoading;
        }

        string ModeLabel(int w, int h)
        {
            string r = Display.Ratio(w, h);
            string tag = "";
            if (w == native.Width && h == native.Height) tag = "   · " + L.T("tag_native");
            else if (r == "4:3") tag = "   · " + L.T("tag_pro");
            else if (native.Height > 0 && (double)w / h < (double)native.Width / native.Height - 0.05) tag = "   · " + L.T("tag_stretched");
            return w + " × " + h + "    " + r + tag;
        }

        void FillFormats(Game g)
        {
            cmbFormat.BeginUpdate();
            cmbFormat.Items.Clear();
            int selIndex = -1;
            HashSet<long> listed = new HashSet<long>();

            // 1) favorites en premier
            foreach (Size f in cfg.Favs)
            {
                if (!listed.Add(f.Width * 100000L + f.Height)) continue;
                bool exists = modes.Any(m => m.W == f.Width && m.H == f.Height);
                string txt = "★  " + ModeLabel(f.Width, f.Height) + (exists ? "" : "   · " + L.T("to_create"));
                int idx = cmbFormat.Items.Add(new FormatItem { W = f.Width, H = f.Height, Fav = true, Text = txt });
                if (f.Width == g.W && f.Height == g.H) selIndex = idx;
            }
            // 2) natif
            int nat = cmbFormat.Items.Add(new FormatItem { W = 0, H = 0, Text = L.F("native_item", native.Width, native.Height) });
            if (g.W <= 0) selIndex = nat;
            // 3) toutes les autres
            List<Mode> sorted = modes
                .OrderBy(m => Display.RatioOrder(m.W, m.H)).ThenByDescending(m => m.W).ThenByDescending(m => m.H).ToList();
            foreach (Mode m in sorted)
            {
                if (!listed.Add(m.W * 100000L + m.H)) continue;
                int idx = cmbFormat.Items.Add(new FormatItem { W = m.W, H = m.H, Text = ModeLabel(m.W, m.H) });
                if (m.W == g.W && m.H == g.H) selIndex = idx;
            }
            if (selIndex < 0)
                selIndex = cmbFormat.Items.Add(new FormatItem { W = g.W, H = g.H, Text = g.W + " × " + g.H + "    " + Display.Ratio(g.W, g.H) + "   · " + L.T("custom") });
            cmbFormat.Items.Add(new FormatItem { W = -1, H = -1, Text = L.T("other_res") });
            cmbFormat.SelectedIndex = selIndex;
            cmbFormat.EndUpdate();
            UpdateFavButton();
        }

        void UpdateFavButton()
        {
            FormatItem it = cmbFormat.SelectedItem as FormatItem;
            bool can = it != null && it.W > 0;
            bool fav = can && cfg.IsFav(it.W, it.H);
            btnFav.Enabled = can;
            btnFav.Text = fav ? "★" : "☆";
            btnFav.ForeColor = fav ? Theme.Gold : Theme.Dim;
            tip.SetToolTip(btnFav, fav ? L.T("fav_remove") : L.T("fav_add"));
        }

        void ToggleFav()
        {
            Game g = Selected();
            FormatItem it = cmbFormat.SelectedItem as FormatItem;
            if (g == null || it == null || it.W <= 0) return;
            if (cfg.IsFav(it.W, it.H)) cfg.Favs.RemoveAll(f => f.Width == it.W && f.Height == it.H);
            else cfg.Favs.Add(new Size(it.W, it.H));
            cfg.Save();
            loading = true; FillFormats(g); loading = false;
            list.Invalidate();
        }

        void FormatChosen()
        {
            Game g = Selected();
            FormatItem it = cmbFormat.SelectedItem as FormatItem;
            if (g == null || it == null) return;
            if (it.W == -1)
            {
                int w = g.W > 0 ? g.W : 1440, h = g.H > 0 ? g.H : 1080;
                if (AskResolution(ref w, ref h)) { g.W = w; g.H = h; cfg.Save(); }
                loading = true; FillFormats(g); loading = false;
            }
            else { g.W = it.W; g.H = it.H; cfg.Save(); }
            list.Invalidate();
        }

        bool AskResolution(ref int w, ref int h)
        {
            using (Form f = new Form())
            {
                f.Text = L.T("custom_res"); f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false; f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(300, 130); f.BackColor = Theme.Bg; f.ForeColor = Theme.Text; f.Font = Font; f.ShowInTaskbar = false;
                NumericUpDown nw = new NumericUpDown { Minimum = 640, Maximum = 7680, Value = Math.Max(640, Math.Min(7680, w)), Location = new Point(20, 34), Width = 110 };
                NumericUpDown nh = new NumericUpDown { Minimum = 480, Maximum = 4320, Value = Math.Max(480, Math.Min(4320, h)), Location = new Point(170, 34), Width = 110 };
                f.Controls.Add(Theme.MakeLabel(L.T("width"), 20, 12, Theme.Dim, 9f, FontStyle.Regular));
                f.Controls.Add(Theme.MakeLabel(L.T("height"), 170, 12, Theme.Dim, 9f, FontStyle.Regular));
                f.Controls.Add(Theme.MakeLabel("×", 142, 36, Theme.Dim, 10f, FontStyle.Regular));
                f.Controls.Add(nw); f.Controls.Add(nh);
                Button ok = Theme.MakeButton("OK", true); ok.Location = new Point(180, 80); ok.Size = new Size(100, 32); ok.DialogResult = DialogResult.OK;
                f.Controls.Add(ok); f.AcceptButton = ok;
                if (f.ShowDialog(this) != DialogResult.OK) return false;
                w = (int)nw.Value; h = (int)nh.Value;
                return true;
            }
        }

        void DefaultFormat(Game g)
        {
            Mode best = modes.FirstOrDefault(m => m.W == 1440 && m.H == 1080)
                     ?? modes.Where(m => Display.Ratio(m.W, m.H) == "4:3" && m.H <= native.Height).OrderByDescending(m => m.W).FirstOrDefault();
            if (best != null) { g.W = best.W; g.H = best.H; }
            else { g.W = 1440; g.H = 1080; }
        }

        void AddGame(string name, string exe)
        {
            exe = Path.GetFileName(exe.Trim());
            if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) exe += ".exe";
            Game existing = cfg.Games.FirstOrDefault(x => x.Matches(Game.Strip(exe)));
            if (existing != null) { txtSearch.Clear(); RebuildList(existing); return; }
            Game g = new Game { Exe = exe, Name = string.IsNullOrEmpty(name) ? Path.GetFileNameWithoutExtension(exe) : name };
            foreach (string p in g.ProcNames) cfg.Ignored.Remove(p);
            if (modes.Count == 0) modes = Display.Modes();
            DefaultFormat(g);
            cfg.Games.Insert(0, g);
            cfg.Save();
            txtSearch.Clear();
            RebuildList(g);
        }

        void RemoveSelected()
        {
            Game g = Selected();
            if (g == null) return;
            int i = list.SelectedIndex;
            foreach (string p in g.ProcNames) cfg.Ignored.Add(p); // le scan ne le rajoutera pas
            cfg.Games.Remove(g);
            cfg.Save();
            Game next = view.Count > 1 ? view[i + 1 < view.Count ? i + 1 : i - 1] : null;
            RebuildList(next);
        }

        ContextMenuStrip BuildAddMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Items.Add(new ToolStripMenuItem(L.T("menu_browse"), null, (s, e) => BrowseExe()));

            ToolStripMenuItem pop = new ToolStripMenuItem(L.T("menu_popular"));
            foreach (string[] p in Popular)
            {
                string n = p[0].StartsWith("@") ? L.T(p[0].Substring(1)) : p[0], x = p[1];
                pop.DropDownItems.Add(new ToolStripMenuItem(n, null, (s, e) => AddGame(n, x)));
            }
            m.Items.Add(pop);

            ToolStripMenuItem run = new ToolStripMenuItem(L.T("menu_running"));
            List<KeyValuePair<string, string>> procs = new List<KeyValuePair<string, string>>();
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            int self = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcesses())
            {
                try
                {
                    if (p.Id != self && p.MainWindowHandle != IntPtr.Zero && !string.IsNullOrEmpty(p.MainWindowTitle)
                        && !p.ProcessName.Equals("explorer", StringComparison.OrdinalIgnoreCase) && seen.Add(p.ProcessName))
                        procs.Add(new KeyValuePair<string, string>(p.MainWindowTitle, p.ProcessName + ".exe"));
                }
                catch { }
                p.Dispose();
            }
            foreach (KeyValuePair<string, string> kv in procs.OrderBy(k => k.Key))
            {
                string title = kv.Key.Length > 45 ? kv.Key.Substring(0, 45) + "…" : kv.Key;
                string exe = kv.Value;
                run.DropDownItems.Add(new ToolStripMenuItem(title + "   (" + exe + ")", null, (s, e) => AddGame(title, exe)));
            }
            if (run.DropDownItems.Count == 0) run.DropDownItems.Add(new ToolStripMenuItem(L.T("none")) { Enabled = false });
            m.Items.Add(run);

            m.Items.Add(new ToolStripSeparator());
            m.Items.Add(new ToolStripMenuItem(L.T("menu_type"), null, (s, e) => TypeExe()));
            return m;
        }

        void BrowseExe()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = L.T("browse_title");
                d.Filter = L.T("browse_filter");
                if (d.ShowDialog(this) != DialogResult.OK) return;
                string name = null;
                try
                {
                    FileVersionInfo v = FileVersionInfo.GetVersionInfo(d.FileName);
                    name = !string.IsNullOrWhiteSpace(v.ProductName) ? v.ProductName : v.FileDescription;
                }
                catch { }
                AddGame(string.IsNullOrWhiteSpace(name) ? null : name.Trim(), d.FileName);
            }
        }

        void TypeExe()
        {
            using (Form f = new Form())
            {
                f.Text = L.T("type_title"); f.FormBorderStyle = FormBorderStyle.FixedDialog;
                f.MaximizeBox = false; f.MinimizeBox = false; f.StartPosition = FormStartPosition.CenterParent;
                f.ClientSize = new Size(360, 120); f.BackColor = Theme.Bg; f.ForeColor = Theme.Text; f.Font = Font; f.ShowInTaskbar = false;
                f.Controls.Add(Theme.MakeLabel(L.T("type_label"), 20, 12, Theme.Dim, 9f, FontStyle.Regular));
                TextBox t = new TextBox { Location = new Point(20, 36), Width = 320 };
                f.Controls.Add(t);
                Button ok = Theme.MakeButton(L.T("add_btn"), true); ok.Location = new Point(240, 74); ok.Size = new Size(100, 32); ok.DialogResult = DialogResult.OK;
                f.Controls.Add(ok); f.AcceptButton = ok;
                if (f.ShowDialog(this) == DialogResult.OK && t.Text.Trim().Length > 0) AddGame(null, t.Text);
            }
        }

        // ---------- rafraichissement en direct
        string lastLiveSig;

        // Appele toutes les 0,5 s : ne redessine que si quelque chose a vraiment change (evite le clignotement).
        public void RefreshLive(string status, bool active)
        {
            string text = "●  " + status;
            if (lblStatus.Text != text) lblStatus.Text = text;
            Color c = active ? Theme.Green : Theme.Dim;
            if (lblStatus.ForeColor != c) lblStatus.ForeColor = c;

            StringBuilder sig = new StringBuilder();
            foreach (Game g in view) sig.Append(app.IsRunning(g) ? '1' : '0');
            string s = sig.ToString();
            if (s != lastLiveSig) { lastLiveSig = s; list.Invalidate(); }
        }

        public void SetScanning(bool on)
        {
            btnScan.Enabled = !on;
            btnScan.Text = on ? L.T("scanning") : L.T("scan");
        }

        public void SetTestCountdown(int seconds)
        {
            btnTest.Text = seconds > 0 ? L.F("test_back", seconds) : L.T("test");
            btnTest.Width = TestWidth(btnTest.Text);
        }

        int TestWidth(string text)
        {
            return Math.Min(336, Math.Max(200, TextRenderer.MeasureText(text, btnTest.Font).Width + 30));
        }

        // ---------- dessin
        string FormatText(Game g)
        {
            if (g.W <= 0) return L.T("native_short");
            return (cfg.IsFav(g.W, g.H) ? "★ " : "") + g.W + " × " + g.H + "  (" + Display.Ratio(g.W, g.H) + ")";
        }

        void DrawGame(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0 || e.Index >= view.Count) return;
            Game g = view[e.Index];
            bool sel = (e.State & DrawItemState.Selected) != 0;
            Rectangle r = e.Bounds;
            Graphics gr = e.Graphics;
            gr.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush b = new SolidBrush(sel ? Theme.Panel2 : Theme.Panel)) gr.FillRectangle(b, r);
            if (sel) using (SolidBrush b = new SolidBrush(Theme.Accent)) gr.FillRectangle(b, r.X, r.Y + 8, 3, r.Height - 16);

            bool running = app.IsRunning(g);
            string pill = running ? L.T("pill_playing") : (!g.Enabled ? L.T("pill_off") : null);
            int pillW = 0;
            if (pill != null)
            {
                Size ts = TextRenderer.MeasureText(pill, fPill);
                pillW = ts.Width + 10;
                Rectangle pr = new Rectangle(r.Right - pillW - 12, r.Y + (r.Height - 20) / 2, pillW, 20);
                Color pc = running ? Theme.Green : Theme.Border;
                using (GraphicsPath p = Theme.Rounded(pr, 9))
                using (SolidBrush b = new SolidBrush(pc)) gr.FillPath(b, p);
                TextRenderer.DrawText(gr, pill, fPill, pr, running ? Color.FromArgb(10, 30, 18) : Theme.Dim,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter);
            }
            int textW = r.Width - 26 - (pillW > 0 ? pillW + 12 : 0);
            TextRenderer.DrawText(gr, g.Name, fBold, new Rectangle(r.X + 14, r.Y + 7, textW, 22),
                g.Enabled ? Theme.Text : Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(gr, FormatText(g), fSmall, new Rectangle(r.X + 14, r.Y + 29, textW, 18),
                g.W > 0 && g.Enabled ? Theme.Accent : Theme.Dim, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using (Pen p = new Pen(Theme.Bg)) gr.DrawLine(p, r.X, r.Bottom - 1, r.Right, r.Bottom - 1);
        }

        void DrawFormat(object sender, DrawItemEventArgs e)
        {
            if (e.Index < 0) return;
            FormatItem it = cmbFormat.Items[e.Index] as FormatItem;
            bool sel = (e.State & DrawItemState.Selected) != 0 && (e.State & DrawItemState.ComboBoxEdit) == 0;
            using (SolidBrush b = new SolidBrush(sel ? Color.FromArgb(70, 50, 20) : Theme.Panel2)) e.Graphics.FillRectangle(b, e.Bounds);
            Color c = Theme.Text;
            if (it != null && it.W == -1) c = Theme.Accent;
            else if (it != null && it.Fav) c = Theme.Gold;
            else if (it != null && it.W > 0 && Display.Ratio(it.W, it.H) == "4:3") c = Color.FromArgb(255, 200, 140);
            TextRenderer.DrawText(e.Graphics, it != null ? it.Text : "", Font, new Rectangle(e.Bounds.X + 6, e.Bounds.Y, e.Bounds.Width - 6, e.Bounds.Height), c,
                TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        }
    }

    // ------------------------------------------------------------------ Programme
    static class Program
    {
        public const string MutexName = "StretchRes_SingleInstance_v1";
        public const string ShowEventName = "StretchRes_Show_v1";
        public const string QuitEventName = "StretchRes_Quit_v1";
        public static string ExePath { get { return Application.ExecutablePath; } }
        public static string InstalledExe { get { return Path.Combine(Config.Dir, "StretchRes.exe"); } }

        [STAThread]
        static void Main(string[] args)
        {
            List<string> a = new List<string>(args);
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            if (!L.Set(Config.Load().Lang)) L.Set(L.Detect());

            if (a.Contains("--selftest")) { SelfTest(a); return; }
            if (a.Contains("--snap"))
            {
                try { Snap(a); }
                catch (Exception ex) { File.WriteAllText((ArgAfter(a, "--snap") ?? "snap.png") + ".err.txt", ex.ToString()); }
                return;
            }

            bool portable = a.Contains("--portable");
            bool installedHere = string.Equals(Path.GetFullPath(ExePath), Path.GetFullPath(InstalledExe), StringComparison.OrdinalIgnoreCase);
            bool created;
            Mutex mutex = new Mutex(true, MutexName, out created);
            if (!created)
            {
                if (portable || installedHere)
                {
                    try { EventWaitHandle.OpenExisting(ShowEventName).Set(); } catch { }
                    return;
                }
                // Mise a jour : on ferme la version installee qui tourne, puis on la remplace.
                if (!CloseRunningInstance(mutex))
                {
                    MessageBox.Show(L.T("err_close_old"), "StretchRes", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
            }
            if (!portable && !installedHere)
            {
                try
                {
                    Directory.CreateDirectory(Config.Dir);
                    for (int i = 0; ; i++)
                    {
                        try { File.Copy(ExePath, InstalledExe, true); break; }
                        catch (IOException) { if (i >= 15) throw; Thread.Sleep(300); }
                    }
                    Startup.Set(true, InstalledExe);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(L.F("err_install", ex.Message), "StretchRes", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return;
                }
                mutex.ReleaseMutex();
                mutex.Dispose();
                Process.Start(InstalledExe, "--show --firstrun");
                return;
            }

            Application.Run(new App(!a.Contains("--tray"), a.Contains("--firstrun")));
            GC.KeepAlive(mutex);
        }

        static bool CloseRunningInstance(Mutex mutex)
        {
            bool asked = false;
            try { EventWaitHandle.OpenExisting(QuitEventName).Set(); asked = true; } catch { }
            try { if (asked && mutex.WaitOne(5000)) return true; }
            catch (AbandonedMutexException) { return true; }

            // ancienne version sans fermeture propre : on arrete le processus installe
            int self = Process.GetCurrentProcess().Id;
            foreach (Process p in Process.GetProcessesByName("StretchRes"))
            {
                try
                {
                    if (p.Id != self && string.Equals(p.MainModule.FileName, InstalledExe, StringComparison.OrdinalIgnoreCase))
                    { p.Kill(); p.WaitForExit(3000); }
                }
                catch { }
                p.Dispose();
            }
            try { if (!mutex.WaitOne(5000)) return false; }
            catch (AbandonedMutexException) { }
            if (!Display.AtNative()) Display.ForceReset();
            return true;
        }

        static string ArgAfter(List<string> a, string key)
        {
            int i = a.IndexOf(key);
            return i >= 0 && i + 1 < a.Count ? a[i + 1] : null;
        }

        // Liste les modes d'affichage sans rien changer.
        static void SelfTest(List<string> a)
        {
            StringBuilder sb = new StringBuilder();
            Size n = Display.NativeSize();
            DEVMODE c = Display.Current();
            sb.AppendLine("Ecran : " + Display.Device);
            sb.AppendLine("Natif : " + n.Width + "x" + n.Height + "  Actuel : " + c.dmPelsWidth + "x" + c.dmPelsHeight + " @" + c.dmDisplayFrequency + "Hz");
            sb.AppendLine("DEVMODE size = " + Marshal.SizeOf(typeof(DEVMODE)) + "  PATH=" + Marshal.SizeOf(typeof(DISPLAYCONFIG_PATH_INFO))
                + "  MODE=" + Marshal.SizeOf(typeof(DISPLAYCONFIG_MODE_INFO)) + "  SRCNAME=" + Marshal.SizeOf(typeof(DISPLAYCONFIG_SOURCE_DEVICE_NAME)));
            sb.AppendLine("Mise a l'echelle actuelle : " + Display.CurrentScaling());
            uint fpid;
            Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out fpid);
            sb.AppendLine("Premier plan : pid " + fpid + " -> " + Native.ProcessNameOf(fpid));
            HashSet<string> pn = Native.ProcessNames();
            sb.AppendLine("Processus vus : " + pn.Count + "  (explorer present : " + pn.Contains("explorer") + ")");
            IntPtr hx = Native.OpenProcess(0x00100000 | 0x1000, false, fpid);
            sb.AppendLine("Suivi du processus : handle " + (hx != IntPtr.Zero ? "OK" : "KO") + ", vivant = " + (hx != IntPtr.Zero && Native.WaitForSingleObject(hx, 0) != 0));
            if (hx != IntPtr.Zero) Native.CloseHandle(hx);
            sb.AppendLine("Validation 1440x1080 etire par GPU (sans appliquer) : code " + Display.ValidateStretch(1440, 1080) + " (0 = OK)");
            sb.AppendLine("Validation 1280x960 etire par GPU (sans appliquer) : code " + Display.ValidateStretch(1280, 960) + " (0 = OK)");
            sb.AppendLine("Validation 1600x1080 etire par GPU (sans appliquer) : code " + Display.ValidateStretch(1600, 1080) + " (0 = OK)");
            sb.AppendLine("--- Jeux trouves :");
            foreach (FoundGame f in Scanner.Scan()) sb.AppendLine("  " + f.Name + "  ->  " + f.Exe);
            sb.AppendLine("--- Modes :");
            foreach (Mode m in Display.Modes().OrderBy(m => Display.RatioOrder(m.W, m.H)).ThenByDescending(m => m.W))
                sb.AppendLine(m.W + "x" + m.H + "  " + Display.Ratio(m.W, m.H) + "  Hz: " + string.Join(",", m.Rates.OrderBy(x => x)));
            File.WriteAllText(ArgAfter(a, "--selftest") ?? "selftest.txt", sb.ToString(), Encoding.UTF8);
        }

        // Capture la fenetre (hors ecran) avec des jeux d'exemple, sans rien enregistrer.
        // --lang xx : dans une langue donnee ; --picker : capture la fenetre de choix de langue.
        static void Snap(List<string> a)
        {
            Config.NoSave = true;
            string lg = ArgAfter(a, "--lang");
            if (lg != null) L.Set(lg);
            if (a.Contains("--picker"))
            {
                using (Form p = LangPicker.Create(L.Code, code => { }))
                {
                    p.StartPosition = FormStartPosition.Manual; p.Location = new Point(-5000, -5000); p.ShowInTaskbar = false;
                    p.Show(); Application.DoEvents();
                    using (Bitmap bmp = new Bitmap(p.Width, p.Height))
                    {
                        p.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                        bmp.Save(ArgAfter(a, "--snap") ?? "snap.png", System.Drawing.Imaging.ImageFormat.Png);
                    }
                }
                return;
            }
            Config c = new Config();
            c.Games.Add(new Game { Name = "Fortnite", Exe = "FortniteClient-Win64-Shipping.exe", W = 1440, H = 1080 });
            c.Games.Add(new Game { Name = "Counter-Strike 2", Exe = "cs2.exe", W = 1280, H = 960 });
            c.Games.Add(new Game { Name = "Rocket League", Exe = "RocketLeague.exe", W = 0, H = 0, Enabled = false });
            App app = new App(c);
            MainForm f = app.Form;
            f.StartPosition = FormStartPosition.Manual;
            f.Location = new Point(-5000, -5000);
            f.ShowInTaskbar = false;
            f.Show();
            f.RefreshAll();
            f.RefreshLive("Fortnite → 1440 × 1080", true);
            Application.DoEvents();
            using (Bitmap bmp = new Bitmap(f.Width, f.Height))
            {
                f.DrawToBitmap(bmp, new Rectangle(0, 0, bmp.Width, bmp.Height));
                bmp.Save(ArgAfter(a, "--snap") ?? "snap.png", System.Drawing.Imaging.ImageFormat.Png);
            }
            app.Exiting = true;
            f.Close();
        }
    }
}
