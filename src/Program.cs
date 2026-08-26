using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Forms;

namespace KJ_FlowForge_CreateKey
{
    public class LicenseEntry
    {
        public string Id = "";
        public string Hash = "";
        public string Owner = "";
        public string ExpiresAt = "";
        public string CreatedAt = "";
        public string Role = "";   // "admin" | ""(일반)
        public string KeyPlain = "";   // 로컬 전용 (keys.local.json)
    }

    public class MainForm : Form
    {
        private static readonly string ExeDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string JsonPath = Path.Combine(ExeDir, "licenses.json");
        private static readonly string LocalKeysPath = Path.Combine(ExeDir, "keys.local.json");
        private static readonly string SettingsPath = Path.Combine(ExeDir, "ui.settings.json");
        private static readonly string BackupDirPath = @"W:\WorkSpace\KJ_FlowForge_Config_Backup";
        public static string TestJsonPath { get { return JsonPath; } }

        // 전체 UI 배율 (폰트/좌표 공통 적용)
        private const float UiScale = 1.8f;
        private static int S(int v) { return (int)Math.Round(v * UiScale); }

        private TabControl tabs;

        // 발급 현황 탭
        private ListView licenseList;
        private Button refreshButton, deleteButton, revokeButton, restoreButton;
        private Button copyKeyButton2;
        private TextBox searchBox;
        private CheckBox filterExpiringCheck, filterRevokedCheck;
        private Button renewButton, copyDetailButton;
        private Button pushRetryButton;
        private Button extend30Button, extend90Button, extend365Button;

        // 키 발급 탭
        private TextBox idBox, ownerBox, resultBox;
        private DateTimePicker expiryPicker;
        private Label typedDateLabel;
        private ComboBox expiryHourBox, expiryMinuteBox;
        private CheckBox expiryTimeCheck;
        private CheckBox expiryEnabledCheck;
        private CheckBox adminCheck;
        private Button generateButton, copyKeyButton;
        private Button cancelRenewButton;
        private string lastKey = "";
        private LicenseEntry lastEntry = null;
        private LicenseEntry renewTarget = null;

        private List<LicenseEntry> entries = new List<LicenseEntry>();
        private List<string> revoked = new List<string>();

        // ===== Ctrl+마우스휠 UI 배율 조절 =====
        private float uiZoom = 1.8f;
        private const float DesignScale = 1.8f;   // 최초 디자인 배율
        private const float ZoomStep = 0.1f;
        private const float ZoomMin = 1.0f;
        private const float ZoomMax = 3.0f;
        private readonly Dictionary<Control, ZoomInfo> zoomInfoMap = new Dictionary<Control, ZoomInfo>();
        private Size baseClientSize;
        private Size baseMinimumSize;
        private string tabsBaseFontFamily = "";
        private float tabsBaseFontSize;
        private FontStyle tabsBaseFontStyle;

        private class ZoomInfo
        {
            public Rectangle Bounds;
            public bool HasOwnFont;
            public string FontFamily = "";
            public float FontSize;
            public FontStyle FontStyle;
            public int[] ColumnWidths;
        }

        private class CtrlWheelFilter : IMessageFilter
        {
            private readonly MainForm owner;
            public CtrlWheelFilter(MainForm owner) { this.owner = owner; }
            public bool PreFilterMessage(ref Message m)
            {
                // WM_MOUSEWHEEL (0x20A) + Ctrl 키 → 줌 처리 후 이벤트 소비
                if (m.Msg == 0x20A && (Control.ModifierKeys & Keys.Control) == Keys.Control)
                {
                    int delta = (short)((long)m.WParam >> 16);
                    owner.ApplyZoom(delta > 0 ? ZoomStep : -ZoomStep);
                    return true;
                }
                return false;
            }
        }

        // 최초 표시 시점의 컨트롤 위치/폰트를 기준으로 저장
        private void CaptureZoomBase()
        {
            zoomInfoMap.Clear();
            CollectZoomInfo(tabs);
        }

        private void CollectZoomInfo(Control root)
        {
            foreach (Control c in root.Controls)
            {
                var info = new ZoomInfo
                {
                    Bounds = c.Bounds,
                    HasOwnFont = true,
                    FontFamily = c.Font.FontFamily.Name,
                    FontSize = c.Font.Size,
                    FontStyle = c.Font.Style,
                };
                var listView = c as ListView;
                if (listView != null)
                {
                    info.ColumnWidths = new int[listView.Columns.Count];
                    for (int i = 0; i < listView.Columns.Count; i++)
                        info.ColumnWidths[i] = listView.Columns[i].Width;
                }
                zoomInfoMap[c] = info;
                if (c.Controls.Count > 0) CollectZoomInfo(c);
            }
        }

        private void ApplyZoom(float delta)
        {
            if (zoomInfoMap.Count == 0) return;
            float newZoom = Math.Max(ZoomMin, Math.Min(ZoomMax, uiZoom + delta));
            if (Math.Abs(newZoom - uiZoom) < 0.001f) return;
            uiZoom = newZoom;
            float factor = uiZoom / DesignScale;

            SuspendAllLayout(this);
            // 탭 컨트롤 자체 폰트(머리글 "발급 현황"/"키 발급" 포함)
            try { tabs.Font = new Font(tabsBaseFontFamily, tabsBaseFontSize * factor, tabsBaseFontStyle); } catch { }
            // 폰트 먼저 변경(라벨 AutoSize 재계산), 이후 위치 복원
            foreach (var pair in zoomInfoMap)
            {
                var c = pair.Key;
                var info = pair.Value;
                try { c.Font = new Font(info.FontFamily, info.FontSize * factor, info.FontStyle); } catch { }
            }
            foreach (var pair in zoomInfoMap)
            {
                var c = pair.Key;
                var b = pair.Value.Bounds;
                c.Bounds = new Rectangle(
                    (int)Math.Round(b.X * factor),
                    (int)Math.Round(b.Y * factor),
                    (int)Math.Round(b.Width * factor),
                    (int)Math.Round(b.Height * factor));
            }
            ClientSize = new Size(
                (int)Math.Round(baseClientSize.Width * factor),
                (int)Math.Round(baseClientSize.Height * factor));
            MinimumSize = new Size(
                (int)Math.Round(baseMinimumSize.Width * factor),
                (int)Math.Round(baseMinimumSize.Height * factor));

            var lv = licenseList;
            if (zoomInfoMap.ContainsKey(lv))
            {
                var widths = zoomInfoMap[lv].ColumnWidths;
                for (int i = 0; i < widths.Length && i < lv.Columns.Count; i++)
                    lv.Columns[i].Width = (int)Math.Round(widths[i] * factor);
            }
            ResumeAllLayout(this);
            AutoFitAllColumns();
            SaveUiSettings();
        }

        private void SuspendAllLayout(Control root)
        {
            root.SuspendLayout();
            foreach (Control c in root.Controls) SuspendAllLayout(c);
        }

        private void ResumeAllLayout(Control root)
        {
            foreach (Control c in root.Controls) ResumeAllLayout(c);
            root.ResumeLayout(true);
        }

        // 발급 현황 모든 열을 내용 길이에 맞게 자동 확장
        private void AutoFitAllColumns()
        {
            if (licenseList == null || licenseList.Columns.Count == 0) return;
            if (licenseList.Items.Count == 0)
            {
                for (int i = 0; i < licenseList.Columns.Count; i++)
                    licenseList.Columns[i].Width = ColumnHeaderMinWidth(i);
                return;
            }
            using (var g = licenseList.CreateGraphics())
            {
                for (int col = 0; col < licenseList.Columns.Count; col++)
                {
                    int maxW = (int)Math.Ceiling(g.MeasureString(licenseList.Columns[col].Text, licenseList.Font).Width) + 30;
                    foreach (ListViewItem item in licenseList.Items)
                    {
                        string text = col < item.SubItems.Count ? item.SubItems[col].Text : "";
                        int w = (int)Math.Ceiling(g.MeasureString(text, licenseList.Font).Width) + 40;
                        if (w > maxW) maxW = w;
                    }
                    licenseList.Columns[col].Width = maxW;
                }
            }
        }

        private int ColumnHeaderMinWidth(int index)
        {
            int[] mins = { S(120), S(100), S(90), S(70), S(60), S(90), S(120) };
            return index < mins.Length ? mins[index] : S(80);
        }

        public MainForm()
        {
            Text = "KJ FlowForge - 라이선스 관리";
            Font = new Font("맑은 고딕", 15f);
            Size = new Size(1380, 1010);
            MinimumSize = new Size(1200, 900);
            StartPosition = FormStartPosition.CenterScreen;
            TryLoadZoom();
            if (Math.Abs(uiZoom - DesignScale) > 0.01f)
            {
                float rf = uiZoom / DesignScale;
                Font = new Font("맑은 고딕", 15f * rf);
                Size = new Size((int)Math.Round(1380 * rf), (int)Math.Round(1010 * rf));
                MinimumSize = new Size((int)Math.Round(1200 * rf), (int)Math.Round(900 * rf));
                baseClientSize = ClientSize;
                baseMinimumSize = MinimumSize;
            }
            else
            {
            baseClientSize = ClientSize;
            baseMinimumSize = MinimumSize;
            }

            tabs = new TabControl { Dock = DockStyle.Fill };
            var issueTab = BuildIssueTab();
            var listTab = BuildListTab();
            tabs.TabPages.Add(listTab);
            tabs.TabPages.Add(issueTab);

            Controls.Add(tabs);
            tabsBaseFontFamily = tabs.Font.FontFamily.Name;
            tabsBaseFontSize = tabs.Font.Size;
            tabsBaseFontStyle = tabs.Font.Style;
            // Ctrl+휠 줌 필터 등록
            var wheelFilter = new CtrlWheelFilter(this);
            Application.AddMessageFilter(wheelFilter);
            FormClosed += (s, e) => Application.RemoveMessageFilter(wheelFilter);
            Shown += (s, e) => CaptureZoomBase();
            FormClosing += (s, e) => SaveUiSettings();
            Resize += (s, e) => AutoFitAllColumns();
            Load += (s, e) => Reload();
            Shown += (s, e) => { BackupLocalKeys(); ShowExpiryAlert(); };
        }

        // ==================== 설정 저장/복원 (배율 기억) ====================

        private void SaveUiSettings()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"zoom\": " + uiZoom.ToString(System.Globalization.CultureInfo.InvariantCulture));
                sb.AppendLine("}");
                File.WriteAllText(SettingsPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private void TryLoadZoom()
        {
            try
            {
                if (!File.Exists(SettingsPath)) return;
                string raw = File.ReadAllText(SettingsPath);
                int i = raw.IndexOf("\"zoom\"");
                if (i < 0) return;
                int colon = raw.IndexOf(':', i);
                int end = colon;
                while (end < raw.Length && (char.IsDigit(raw[end]) || raw[end] == '.' || raw[end] == '-')) end++;
                float z;
                if (float.TryParse(raw.Substring(colon + 1, end - colon - 1).Trim(),
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out z))
                {
                    uiZoom = Math.Max(ZoomMin, Math.Min(ZoomMax, z));
                }
            }
            catch { }
        }

        // ==================== 발급 현황 탭 ====================

        private TabPage BuildListTab()
        {
            var page = new TabPage("발급 현황");

            var topPanel = new FlowLayoutPanel
            {
                Location = new Point(S(12), S(12)),
                Size = new Size(S(720), S(42)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            refreshButton = new Button { Text = "새로고침", Width = S(90), Height = S(32) };
            refreshButton.Click += (s, e) => Reload();
            deleteButton = new Button { Text = "삭제", Width = S(70), Height = S(32), Enabled = false };
            deleteButton.Click += (s, e) => DeleteSelected();
            revokeButton = new Button { Text = "폐기", Width = S(70), Height = S(32), Enabled = false };
            revokeButton.Click += (s, e) => ToggleRevoke(true);
            restoreButton = new Button { Text = "복원", Width = S(70), Height = S(32), Enabled = false };
            restoreButton.Click += (s, e) => ToggleRevoke(false);
            copyKeyButton2 = new Button { Text = "키 복사", Width = S(80), Height = S(32), Enabled = false };
            copyKeyButton2.Click += (s, e) => CopySelectedKeys();
            renewButton = new Button { Text = "갱신", Width = S(70), Height = S(32), Enabled = false };
            renewButton.Click += (s, e) => StartRenewSelected();
            copyDetailButton = new Button { Text = "상세 복사", Width = S(100), Height = S(32), Enabled = false };
            copyDetailButton.Click += (s, e) => CopySelectedDetails();
            pushRetryButton = new Button { Text = "푸시 재시도", Width = S(110), Height = S(32) };
            pushRetryButton.Click += (s, e) => RetryPush();
            topPanel.Controls.AddRange(new Control[] { refreshButton, deleteButton, revokeButton, restoreButton,
                copyKeyButton2, renewButton, copyDetailButton, pushRetryButton });

            // 검색 + 필터 행
            var filterPanel = new FlowLayoutPanel
            {
                Location = new Point(S(12), S(60)),
                Size = new Size(S(720), S(40)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            searchBox = new TextBox { Width = S(280) };
            searchBox.TextChanged += (s, e) => RenderList();
            filterExpiringCheck = new CheckBox { Text = "만료 임박(7일)만", AutoSize = true };
            filterExpiringCheck.CheckedChanged += (s, e) => RenderList();
            filterRevokedCheck = new CheckBox { Text = "폐기된 것만", AutoSize = true };
            filterRevokedCheck.CheckedChanged += (s, e) => RenderList();
            filterPanel.Controls.AddRange(new Control[] { searchBox, filterExpiringCheck, filterRevokedCheck });

            // 일괄 연장 행
            var extendPanel = new FlowLayoutPanel
            {
                Location = new Point(S(12), S(106)),
                Size = new Size(S(720), S(40)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            extend30Button = new Button { Text = "+30일 연장", Width = S(110), Height = S(32), Enabled = false };
            extend30Button.Click += (s, e) => ExtendSelected(30);
            extend90Button = new Button { Text = "+90일 연장", Width = S(110), Height = S(32), Enabled = false };
            extend90Button.Click += (s, e) => ExtendSelected(90);
            extend365Button = new Button { Text = "+365일 연장", Width = S(120), Height = S(32), Enabled = false };
            extend365Button.Click += (s, e) => ExtendSelected(365);
            extendPanel.Controls.AddRange(new Control[] { extend30Button, extend90Button, extend365Button });

            licenseList = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                Location = new Point(S(12), S(152)),
                Size = new Size(S(720), S(328)),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font("Consolas", 17f),
            };
            licenseList.Columns.Add("키 ID", 300);
            licenseList.Columns.Add("사용자", 250);
            licenseList.Columns.Add("만료일", 180);
            licenseList.Columns.Add("상태", 140);
            licenseList.Columns.Add("역할", 90);
            licenseList.Columns.Add("생성일", 180);
            licenseList.Columns.Add("발급 키", 430);
            licenseList.SelectedIndexChanged += OnListSelectionChanged;
            licenseList.DoubleClick += (s, e) => CopySelectedKeys();

            var hintLabel = new Label
            {
                Text = "※ 삭제/폐기/복원 시 자동으로 커밋 & 푸시됩니다.  |  키를 복사하려면 행 선택 후 [키 복사] 또는 행 더블클릭",
                Location = new Point(S(12), S(484)),
                AutoSize = true,
                ForeColor = Color.Gray,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };

            page.Controls.AddRange(new Control[] { topPanel, filterPanel, licenseList, hintLabel });
            return page;
        }

        private void OnListSelectionChanged(object sender, EventArgs e)
        {
            if (licenseList.SelectedItems.Count == 0)
            {
                deleteButton.Enabled = revokeButton.Enabled = restoreButton.Enabled = false;
                copyKeyButton2.Enabled = false;
                renewButton.Enabled = false;
                copyDetailButton.Enabled = false;
                extend30Button.Enabled = extend90Button.Enabled = extend365Button.Enabled = false;
                return;
            }
            var entry = GetSelected();
            bool isRevoked = entry != null && revoked.Contains(entry.Id);
            deleteButton.Enabled = true;
            copyKeyButton2.Enabled = true;
            renewButton.Enabled = true;
            copyDetailButton.Enabled = true;
            extend30Button.Enabled = extend90Button.Enabled = extend365Button.Enabled = true;
            revokeButton.Enabled = !isRevoked;
            restoreButton.Enabled = isRevoked;
        }

        private LicenseEntry GetSelected()
        {
            if (licenseList.SelectedItems.Count == 0) return null;
            string id = licenseList.SelectedItems[0].SubItems[0].Text;
            return entries.FirstOrDefault(x => x.Id == id);
        }

        private List<LicenseEntry> GetSelectedList()
        {
            var result = new List<LicenseEntry>();
            foreach (ListViewItem item in licenseList.SelectedItems)
            {
                string id = item.SubItems[0].Text;
                var entry = entries.FirstOrDefault(x => x.Id == id);
                if (entry != null) result.Add(entry);
            }
            return result;
        }

        private void Reload()
        {
            entries.Clear();
            revoked.Clear();
            try
            {
                string raw = File.ReadAllText(JsonPath);
                int pos = 0;
                SkipWs(raw, ref pos);
                if (pos < raw.Length && raw[pos] == '[')
                {
                    var arr = ParseArray(raw, ref pos);
                    foreach (var item in arr)
                        entries.Add(ParseEntry((Dictionary<string, object>)item));
                }
                else
                {
                    var node = ParseObject(raw, ref pos);
                    if (node.ContainsKey("keys") && node["keys"] is List<object>)
                    {
                        foreach (var item in (List<object>)node["keys"])
                            entries.Add(ParseEntry((Dictionary<string, object>)item));
                    }
                    else
                    {
                        // 기존 단일 객체 형식 → 자동 마이그레이션
                        entries.Add(ParseEntry(node));
                    }
                    if (node.ContainsKey("revoked") && node["revoked"] is List<object>)
                    {
                        foreach (var r in (List<object>)node["revoked"]) revoked.Add(r.ToString());
                    }
                }
            }
            catch { /* 파일 없음 또는 파싱 실패 */ }
            LoadLocalKeys();
            RenderList();
        }

        private void LoadLocalKeys()
        {
            try
            {
                if (!File.Exists(LocalKeysPath)) return;
                string raw = File.ReadAllText(LocalKeysPath);
                int pos = 0;
                SkipWs(raw, ref pos);
                var node = ParseObject(raw, ref pos);
                if (node.ContainsKey("keys") && node["keys"] is List<object>)
                {
                    foreach (var item in (List<object>)node["keys"])
                    {
                        var obj = (Dictionary<string, object>)item;
                        string id = Str(obj, "id");
                        string key = Str(obj, "key");
                        var entry = entries.FirstOrDefault(x => x.Id == id);
                        if (entry != null) entry.KeyPlain = key;
                    }
                }
            }
            catch { /* 로컬 파일 없음/손상 무시 */ }
        }

        private void SaveLocalKey(LicenseEntry en)
        {
            try
            {
                var all = new List<Dictionary<string, object>>();
                if (File.Exists(LocalKeysPath))
                {
                    string raw = File.ReadAllText(LocalKeysPath);
                    int pos = 0;
                    SkipWs(raw, ref pos);
                    var node = ParseObject(raw, ref pos);
                    if (node.ContainsKey("keys") && node["keys"] is List<object>)
                    {
                        foreach (var item in (List<object>)node["keys"])
                            all.Add((Dictionary<string, object>)item);
                    }
                }
                all.RemoveAll(x => Str(x, "id") == en.Id);
                var record = new Dictionary<string, object>
                {
                    { "id", en.Id },
                    { "key", en.KeyPlain },
                    { "createdAt", en.CreatedAt },
                };
                all.Add(record);

                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("  \"keys\": [");
                for (int i = 0; i < all.Count; i++)
                {
                    var rec = all[i];
                    sb.Append("    { \"id\": \"" + Escape(Str(rec, "id")) + "\", \"key\": \"" + Escape(Str(rec, "key"))
                        + "\", \"createdAt\": \"" + Escape(Str(rec, "createdAt")) + "\" }");
                    sb.AppendLine(i < all.Count - 1 ? "," : "");
                }
                sb.AppendLine("  ]");
                sb.Append("}");
                File.WriteAllText(LocalKeysPath, sb.ToString() + "\n", Encoding.UTF8);
            }
            catch { /* 저장 실패 무시 */ }
        }

        private void RenderList()
        {
            licenseList.Items.Clear();
            DateTime now = DateTime.Today;
            string query = searchBox != null ? searchBox.Text.Trim().ToLowerInvariant() : "";
            foreach (var en in entries)
            {
                string status;
                Color color = Color.Black;
                DateTime expDt;
                bool isRevoked = revoked.Contains(en.Id);
                int daysLeft = int.MinValue;
                if (en.ExpiresAt.Length > 0 && DateTime.TryParse(en.ExpiresAt, out expDt))
                    daysLeft = (int)(expDt - now).TotalDays;

                // 검색 필터 (ID / 사용자 / 키)
                if (query.Length > 0)
                {
                    bool hit = en.Id.ToLowerInvariant().Contains(query)
                        || en.Owner.ToLowerInvariant().Contains(query)
                        || en.KeyPlain.ToLowerInvariant().Contains(query);
                    if (!hit) continue;
                }
                // 만료 임박 필터: 유효하고 D-7 이내인 키만
                if (filterExpiringCheck != null && filterExpiringCheck.Checked)
                {
                    if (isRevoked || daysLeft == int.MinValue || daysLeft < 0 || daysLeft > 7) continue;
                }
                // 폐기 필터: 폐기된 키만
                if (filterRevokedCheck != null && filterRevokedCheck.Checked && !isRevoked) continue;

                if (isRevoked) { status = "폐기됨"; color = Color.DarkRed; }
                else if (en.ExpiresAt.Length > 0 && DateTime.TryParse(en.ExpiresAt, out expDt))
                {
                    daysLeft = (int)(expDt - now).TotalDays;
                    if (daysLeft < 0) { status = "만료"; color = Color.Red; }
                    else if (daysLeft <= 7) { status = "D-" + daysLeft; color = Color.Orange; }
                    else status = "유효";
                }
                else status = "무기한";

                var item = new ListViewItem(en.Id);
                item.SubItems.Add(en.Owner);
                item.SubItems.Add(en.ExpiresAt.Length > 0 ? en.ExpiresAt : "-");
                item.SubItems.Add(status);
                item.SubItems.Add(en.Role == "admin" ? "관리자" : "일반");
                item.SubItems.Add(en.CreatedAt.Length > 0 ? en.CreatedAt : "-");
                item.SubItems.Add(en.KeyPlain.Length > 0 ? en.KeyPlain : "(로컬 기록 없음)");
                if (en.Role == "admin" && !isRevoked) color = Color.Firebrick;
                item.ForeColor = color;
                licenseList.Items.Add(item);
            }
            FitWindowToKeyContent();
        }

        // 발급 키 내용이 다 보이도록 첫 렌더링 시 창/열 폭을 자동 맞춤 (1회)
        private bool windowAutoFitted = false;
        private void FitWindowToKeyContent()
        {
            if (windowAutoFitted || licenseList.Items.Count == 0) return;
            windowAutoFitted = true;
            // 열 폭 합계가 ListView 안에 다 들어가도록 창 폭 계산
            int totalColumns = 0;
            foreach (ColumnHeader col in licenseList.Columns) totalColumns += col.Width;
            int neededLvWidth = totalColumns + SystemInformation.VerticalScrollBarWidth + 8;
            int currentLvWidth = licenseList.Width;
            int deficit = neededLvWidth - currentLvWidth;
            if (deficit > 0)
            {
                // 창을 늘려도 화면 밖으로 나가지 않게 상한 적용
                Rectangle workArea = Screen.FromControl(this).WorkingArea;
                int maxFormW = workArea.Width - 16;
                int newClientW = ClientSize.Width + deficit;
                if (newClientW + 16 > maxFormW)
                {
                    newClientW = Math.Max(0, maxFormW - 16);
                }
                ClientSize = new Size(newClientW, ClientSize.Height);
                Location = new Point(
                    Math.Max(workArea.Left, Location.X - ((newClientW - ClientSize.Width) / 2)),
                    Location.Y);
            }
        }

        private void CopySelectedKeys()
        {
            var selected = GetSelectedList();
            if (selected.Count == 0) return;
            var lines = new List<string>();
            foreach (var en in selected)
            {
                if (en.KeyPlain.Length > 0)
                    lines.Add(en.KeyPlain);
            }
            if (lines.Count == 0)
            {
                MessageBox.Show("선택한 항목에 로컬 키 기록이 없습니다.\n(프로그램에서 발급된 키만 표시됩니다)",
                    "복사 불가", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            Clipboard.SetText(string.Join(Environment.NewLine, lines.ToArray()));
        }

        private async void DeleteSelected()
        {
            var selected = GetSelectedList();
            if (selected.Count == 0) return;
            string summary;
            if (selected.Count == 1)
                summary = "키 '" + selected[0].Id + "' (" + selected[0].Owner + ")";
            else
                summary = "키 " + selected.Count + "개 (" + string.Join(", ", selected.ConvertAll(x => x.Id).ToArray()) + ")";
            var confirm = MessageBox.Show(
                summary + " 을/를 완전히 삭제할까요?\n이 작업은 커밋 & 푸시됩니다.",
                "삭제 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
            var removedIds = selected.ConvertAll(x => x.Id);
            entries.RemoveAll(delegate(LicenseEntry x) { return removedIds.Contains(x.Id); });
            foreach (var en in selected) SaveLocalKeyRemoval(en.Id);
            string commitMsg;
            if (removedIds.Count == 1)
                commitMsg = "remove license '" + removedIds[0] + "' (" + selected[0].Owner + ")";
            else
                commitMsg = "remove " + removedIds.Count + " licenses (" + string.Join(", ", removedIds.ToArray()) + ")";
            await SaveAndPush(commitMsg);
            RenderList();
        }

        private void SaveLocalKeyRemoval(string id)
        {
            var dummy = new LicenseEntry { Id = id, KeyPlain = "", CreatedAt = "" };
            SaveLocalKey(dummy);
        }

        // ==================== 만료 알림 ====================

        private void ShowExpiryAlert()
        {
            DateTime now = DateTime.Today;
            var soon = new List<string>();
            var expired = new List<string>();
            foreach (var en in entries)
            {
                if (revoked.Contains(en.Id)) continue;
                DateTime expDt;
                if (en.ExpiresAt.Length == 0 || !DateTime.TryParse(en.ExpiresAt, out expDt)) continue;
                int daysLeft = (int)(expDt - now).TotalDays;
                if (daysLeft < 0)
                    expired.Add(en.Id + " (" + en.Owner + ") - " + en.ExpiresAt);
                else if (daysLeft <= 7)
                    soon.Add(en.Id + " (" + en.Owner + ") - D-" + daysLeft + " (" + en.ExpiresAt + ")");
            }
            if (soon.Count == 0 && expired.Count == 0) return;
            var sb = new StringBuilder();
            if (expired.Count > 0)
                sb.AppendLine("만료된 키 " + expired.Count + "개:").AppendLine(string.Join("\n", expired.ToArray())).AppendLine();
            if (soon.Count > 0)
                sb.AppendLine("만료 임박 키 " + soon.Count + "개:").Append(string.Join("\n", soon.ToArray()));
            MessageBox.Show(sb.ToString(), "만료 알림",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        // ==================== 키 갱신(연장) ====================

        private void StartRenewSelected()
        {
            var entry = GetSelected();
            if (entry == null) return;
            renewTarget = entry;
            tabs.SelectedTab = tabs.TabPages[1]; // 키 발급 탭으로 전환
            idBox.Text = entry.Id;
            idBox.ReadOnly = true;               // 갱신 중에는 ID 변경 불가
            ownerBox.Text = entry.Owner;
            adminCheck.Checked = entry.Role == "admin";   // 기존 role 유지 (변경 가능)
            bool entryHasExpiry = entry.ExpiresAt.Length > 0;
            expiryEnabledCheck.Checked = entryHasExpiry;  // 기존 만료일 유무 반영
            cancelRenewButton.Enabled = true;
            cancelRenewButton.Visible = true;
            resultBox.Text = "";
            resultBox.Visible = false;   // 갱신 모드 진입 시에는 결과창 숨김 유지
            generateButton.Text = "갱신 & 저장소 반영";
        }

        private void CancelRenew()
        {
            renewTarget = null;
            idBox.ReadOnly = false;
            generateButton.Text = "키 생성 & 저장소 반영";
            adminCheck.Checked = false;
            expiryEnabledCheck.Checked = true;
            cancelRenewButton.Enabled = false;
            cancelRenewButton.Visible = false;
            resultBox.Text = "";
            resultBox.Visible = false;
        }

        // ==================== 상세 복사 ====================

        // ==================== 일괄 연장 ====================

        private async void ExtendSelected(int days)
        {
            var selected = GetSelectedList();
            if (selected.Count == 0) return;

            // 새 만료일 계산: 기존 만료일(또는 오늘) 기준으로 연장, 과거 만료는 오늘부터
            DateTime now = DateTime.Now;
            var changes = new List<string>();
            foreach (var en in selected)
            {
                if (revoked.Contains(en.Id)) continue;   // 폐기된 키는 연장 제외
                DateTime baseDate = now;
                DateTime expDt;
                if (en.ExpiresAt.Length > 0 && DateTime.TryParse(en.ExpiresAt, out expDt) && expDt > baseDate)
                    baseDate = expDt;
                en.ExpiresAt = baseDate.AddDays(days).ToString("yyyy-MM-dd HH:mm");
                changes.Add(en.Id + ": -> " + en.ExpiresAt);
            }
            if (changes.Count == 0)
            {
                MessageBox.Show("연장할 수 있는(폐기되지 않은) 선택 항목이 없습니다.", "일괄 연장",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            var confirm = MessageBox.Show(
                "선택한 " + changes.Count + "개 키를 +" + days + "일 연장할까요?\n\n"
                + string.Join("\n", changes.ToArray()) + "\n\n이 작업은 커밋 & 푸시됩니다.",
                "일괄 연장 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes)
            {
                Reload();   // 취소 시 변경 롤백
                return;
            }
            string commitMsg = "extend " + changes.Count + " license(s) by " + days + " days";
            await SaveAndPush(commitMsg);
            RenderList();
        }

        // ==================== 푸시 재시도 ====================

        private async void RetryPush()
        {
            pushRetryButton.Enabled = false;
            pushRetryButton.Text = "확인 중...";
            string repoDir = Path.GetDirectoryName(JsonPath);
            var output = new StringBuilder();
            int aheadCount = -1;
            try
            {
                var psi = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "rev-list --count origin/main..main",
                    WorkingDirectory = repoDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };
                using (var p = System.Diagnostics.Process.Start(psi))
                {
                    string stdout = p.StandardOutput.ReadToEnd().Trim();
                    p.WaitForExit(10000);
                    int.TryParse(stdout, out aheadCount);
                }
            }
            catch { }

            if (aheadCount == 0)
            {
                pushRetryButton.Enabled = true;
                pushRetryButton.Text = "푸시 재시도";
                MessageBox.Show("밀려난 커밋이 없습니다. 모두 푸시된 상태입니다.", "푸시 재시도",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }

            int exit = RunGitCapture(repoDir, "push origin main", output);
            pushRetryButton.Enabled = true;
            pushRetryButton.Text = "푸시 재시도";
            if (exit == 0 && aheadCount > 0)
            {
                MessageBox.Show(aheadCount + "개의 밀려난 커밋을 푸시했습니다.", "푸시 재시도",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
            }
            else if (exit != 0)
            {
                MessageBox.Show("푸시 실패. 네트워크를 확인 후 다시 시도하세요.\n\n=== Git 출력 ===\n" + output.ToString(),
                    "푸시 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }

        private void CopySelectedDetails()
        {
            var selected = GetSelectedList();
            if (selected.Count == 0) return;
            var lines = new List<string>();
            foreach (var en in selected)
            {
                string line = "ID: " + en.Id + " / 사용자: " + en.Owner + " / 만료: "
                            + (en.ExpiresAt.Length > 0 ? en.ExpiresAt : "무기한")
                            + " / 역할: " + (en.Role == "admin" ? "관리자" : "일반")
                            + (en.KeyPlain.Length > 0 ? " / 키: " + en.KeyPlain : "");
                lines.Add(line);
            }
            Clipboard.SetText(string.Join(Environment.NewLine, lines.ToArray()));
        }

        // ==================== 로컬 키 백업 ====================

        private void BackupLocalKeys()
        {
            try
            {
                if (!File.Exists(LocalKeysPath)) return;
                Directory.CreateDirectory(BackupDirPath);
                string stamp = DateTime.Now.ToString("yyyy-MM-dd");
                File.Copy(LocalKeysPath, Path.Combine(BackupDirPath, "keys.local." + stamp + ".json"), true);
            }
            catch { /* 백업 실패는 조용히 무시 */ }
        }

        private async void ToggleRevoke(bool doRevoke)
        {
            var entry = GetSelected();
            if (entry == null) return;
            string action = doRevoke ? "폐기" : "복원";
            string detail = doRevoke
                ? "폐기된 키는 Hub에서 즉시 사용이 거부됩니다."
                : "복원하면 해당 키를 다시 사용할 수 있게 됩니다.";
            var confirm = MessageBox.Show(
                "키 '" + entry.Id + "' (" + entry.Owner + ") 을/를 " + action + "할까요?\n"
                + detail + "\n\n이 작업은 커밋 & 푸시됩니다.",
                action + " 확인", MessageBoxButtons.YesNo,
                doRevoke ? MessageBoxIcon.Warning : MessageBoxIcon.Question);
            if (confirm != DialogResult.Yes) return;
            if (doRevoke)
            {
                if (!revoked.Contains(entry.Id)) revoked.Add(entry.Id);
                await SaveAndPush("revoke license '" + entry.Id + "'");
            }
            else
            {
                revoked.Remove(entry.Id);
                await SaveAndPush("restore license '" + entry.Id + "'");
            }
            RenderList();
            OnListSelectionChanged(null, null);
        }

        // ==================== 키 발급 탭 ====================

        private TabPage BuildIssueTab()
        {
            var page = new TabPage("키 발급");

            var label1 = new Label { Text = "키 ID (구분용, 영문 권장)", Location = new Point(S(20), S(20)), AutoSize = true };
            idBox = new TextBox { Location = new Point(S(20), S(43)), Width = S(500) };
            // 키 ID: 영문/숫자/하이픈만 허용 (붙여넣기 포함)
            idBox.KeyPress += (s, e) =>
            {
                char c = e.KeyChar;
                bool ok = char.IsControl(c) || (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z')
                       || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) { e.Handled = true; }
            };

            var label2 = new Label { Text = "사용자 이름", Location = new Point(S(20), S(75)), AutoSize = true };
            ownerBox = new TextBox { Location = new Point(S(20), S(98)), Width = S(500) };

            expiryEnabledCheck = new CheckBox
            {
                Text = "만료일 지정 (체크 해제 시 무기한)",
                Location = new Point(S(20), S(128)),
                AutoSize = true,
                Checked = true,
            };
            expiryPicker = new DateTimePicker
            {
                Location = new Point(S(20), S(153)),
                Width = S(150),
                Format = DateTimePickerFormat.Custom,
                CustomFormat = "yyyy-MM-dd",
                MinDate = DateTime.Today,
                Value = DateTime.Today.AddDays(30),
            };
            // 숫자 타이핑을 가로채 8자리(yyyyMMdd)가 모이면 한 번에 적용.
            // 기본 자리별 입력은 중간값이 MinDate(오늘)보다 작으면 리셋되므로 이 방식 사용.
            expiryPicker.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.V)
                {
                    ApplyTypedDate(Clipboard.GetText());
                    UpdateTypedDateLabel();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    return;
                }
                if (e.KeyCode >= Keys.D0 && e.KeyCode <= Keys.D9 && !e.Control && !e.Alt)
                {
                    typedDigits += (char)('0' + (e.KeyCode - Keys.D0));
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    UpdateTypedDateLabel();
                    if (typedDigits.Length == 8) { ApplyTypedDate(typedDigits); typedDigits = ""; UpdateTypedDateLabel(); }
                    return;
                }
                if (e.KeyCode >= Keys.NumPad0 && e.KeyCode <= Keys.NumPad9 && !e.Control && !e.Alt)
                {
                    typedDigits += (char)('0' + (e.KeyCode - Keys.NumPad0));
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                    UpdateTypedDateLabel();
                    if (typedDigits.Length == 8) { ApplyTypedDate(typedDigits); typedDigits = ""; UpdateTypedDateLabel(); }
                    return;
                }
                if (e.KeyCode == Keys.Back || e.KeyCode == Keys.Escape)
                {
                    typedDigits = "";
                    UpdateTypedDateLabel();
                    e.Handled = true;
                    if (e.KeyCode == Keys.Escape) e.SuppressKeyPress = true;
                }
            };
            expiryPicker.LostFocus += (s, e) => { typedDigits = ""; UpdateTypedDateLabel(); };
            BuildIssueTabControls();
            page.Controls.AddRange(new Control[] { label1, idBox, label2, ownerBox, expiryEnabledCheck, expiryPicker,
                adminCheck,
                typedDateLabel,
                expiryTimeCheck, expiryHourBox, expiryMinuteBox,
                generateButton, copyKeyButton, cancelRenewButton, resultBox });
            return page;
        }

        private string typedDigits = "";

        private void UpdateTypedDateLabel()
        {
            string d = typedDigits ?? "";
            if (d.Length == 0)
            {
                typedDateLabel.Text = "";
                return;
            }
            string year = d.Substring(0, Math.Min(4, d.Length));
            string month = d.Length > 4 ? d.Substring(4, Math.Min(2, d.Length - 4)) : "__";
            string day = d.Length > 6 ? d.Substring(6, Math.Min(2, d.Length - 6)) : "__";
            if (month.Length == 0) month = "_";
            if (day.Length == 0) day = "_";
            typedDateLabel.Text = "입력 중: " + year + "-" + month + "-" + day;
        }

        private void ApplyTypedDate(string rawInput)
        {
            if (rawInput == null) return;
            string digits = "";
            foreach (char c in rawInput) { if (c >= '0' && c <= '9') digits += c; }
            if (digits.Length != 8) return;
            DateTime parsed;
            if (DateTime.TryParseExact(digits, "yyyyMMdd",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out parsed))
            {
                if (parsed < DateTime.Today)
                {
                    MessageBox.Show("과거 날짜는 선택할 수 없습니다.", "만료일 오류",
                        MessageBoxButtons.OK, MessageBoxIcon.Warning);
                }
                else
                {
                    expiryPicker.Value = parsed;
                }
            }
            else
            {
                MessageBox.Show("유효한 날짜가 아닙니다: " + digits, "만료일 오류",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        }

        private void BuildIssueTabControls()
        {
            typedDateLabel = new Label
            {
                Text = "",
                Location = new Point(S(20), S(182)),
                AutoSize = true,
                ForeColor = SystemColors.HotTrack,
                Font = new Font("Consolas", 17f, FontStyle.Bold),
            };
            expiryTimeCheck = new CheckBox
            {
                Text = "시간 지정",
                Location = new Point(S(180), S(155)),
                AutoSize = true,
            };
            adminCheck = new CheckBox
            {
                Text = "관리자",
                Location = new Point(S(410), S(155)),
                AutoSize = true,
                ForeColor = Color.Firebrick,
            };
            expiryHourBox = new ComboBox
            {
                Location = new Point(S(265), S(153)),
                Width = S(60),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Enabled = false,
            };
            for (int h = 0; h < 24; h++) expiryHourBox.Items.Add(h.ToString("00"));
            expiryMinuteBox = new ComboBox
            {
                Location = new Point(S(330), S(153)),
                Width = S(60),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Enabled = false,
            };
            for (int m = 0; m < 60; m += 5) expiryMinuteBox.Items.Add(m.ToString("00"));
            expiryHourBox.SelectedIndex = 23;
            expiryMinuteBox.SelectedIndex = 11; // 23:55
            expiryTimeCheck.CheckedChanged += (s, e) =>
            {
                expiryHourBox.Enabled = expiryTimeCheck.Checked;
                expiryMinuteBox.Enabled = expiryTimeCheck.Checked;
            };
            // 만료일 미지정(무기한) 시 달력/시간 입력 전체 비활성화
            expiryEnabledCheck.CheckedChanged += (s, e) =>
            {
                bool on = expiryEnabledCheck.Checked;
                expiryPicker.Enabled = on;
                expiryTimeCheck.Enabled = on;
                expiryHourBox.Enabled = on && expiryTimeCheck.Checked;
                expiryMinuteBox.Enabled = on && expiryTimeCheck.Checked;
            };

            generateButton = new Button
            {
                Text = "키 생성 & 저장소 반영",
                Location = new Point(S(20), S(205)),
                Width = S(180),
                Height = S(36),
            };
            // 다른 컨트롤과 동일한 크기(폼 기본 폰트) + 볼드만 적용
            generateButton.Font = new Font(Font, FontStyle.Bold);
            generateButton.Click += OnGenerate;

            copyKeyButton = new Button { Text = "키 복사", Location = new Point(S(210), S(207)), Width = S(90), Height = S(32), Enabled = false };
            copyKeyButton.Click += (s, e) => { if (lastKey.Length > 0) Clipboard.SetText(lastKey); };

            cancelRenewButton = new Button
            {
                Text = "갱신 취소",
                Location = new Point(S(310), S(207)),
                Size = new Size(S(100), S(32)),
                Visible = false,
            };
            cancelRenewButton.Click += (s, e) => CancelRenew();

            resultBox = new TextBox
            {
                Location = new Point(S(20), S(250)),
                Width = 500,
                Height = 220,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 17f),
                Visible = false,
            };

        }

        private async void OnGenerate(object sender, EventArgs e)
        {
            string id = idBox.Text.Trim();
            string owner = ownerBox.Text.Trim();
            bool hasExpiry = expiryEnabledCheck.Checked;
            DateTime expiryDate = expiryPicker.Value.Date;
            if (hasExpiry && expiryTimeCheck.Checked)
            {
                expiryDate = expiryDate.AddHours(expiryHourBox.SelectedIndex)
                                     .AddMinutes(expiryMinuteBox.SelectedIndex);
            }
            else if (hasExpiry)
            {
                // 시간 미지정 시 해당 날짜의 끝(23:59)까지 유효하도록 처리
                expiryDate = expiryDate.AddDays(1).AddSeconds(-1);
            }
            string expiry = hasExpiry ? expiryDate.ToString("yyyy-MM-dd") : "";
            if (hasExpiry && expiryTimeCheck.Checked)
                expiry += " " + expiryDate.ToString("HH:mm");

            if (id.Length == 0 || owner.Length == 0)
            {
                MessageBox.Show("키 ID와 사용자 이름을 모두 입력해 주세요.", "입력 필요",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            bool idValidNow = true;
            foreach (char c in id)
            {
                bool ok = (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_';
                if (!ok) { idValidNow = false; break; }
            }
            if (!idValidNow)
            {
                MessageBox.Show("키 ID는 영문, 숫자, 하이픈(-), 밑줄(_)만 사용할 수 있습니다.", "ID 규칙 위반",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (hasExpiry && expiryDate < DateTime.Now)
            {
                MessageBox.Show("만료일이 현재 시각보다 이전입니다. 오늘 이후 날짜를 선택해 주세요.",
                    "만료일 오류",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (entries.Any(x => x.Id == id))
            {
                if (renewTarget != null && renewTarget.Id == id)
                {
                    // 갱신 모드: 자기 자신 ID는 허용
                }
                else
                {
                MessageBox.Show("'" + id + "' 는 이미 존재하는 키 ID입니다. 다른 ID를 입력해 주세요.", "중복",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
                }
            }

            generateButton.Enabled = false;
            generateButton.Text = "처리 중...";

            byte[] bytes = new byte[18];
            using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
            string hex = BitConverter.ToString(bytes).Replace("-", "");
            var sb = new StringBuilder("KJFF-");
            for (int i = 0; i < hex.Length; i += 6)
            {
                if (i > 0) sb.Append('-');
                sb.Append(hex.Substring(i, Math.Min(6, hex.Length - i)));
            }
            lastKey = sb.ToString();

            bool isRenew = renewTarget != null;
            using (var sha = SHA256.Create())
            {
                byte[] hashBytes = sha.ComputeHash(Encoding.UTF8.GetBytes(lastKey));
            lastEntry = new LicenseEntry
                {
                    Id = id,
                    Hash = BitConverter.ToString(hashBytes).Replace("-", "").ToLowerInvariant(),
                    Owner = owner,
                    ExpiresAt = expiry,
                    CreatedAt = DateTime.Now.ToString("yyyy-MM-dd"),
                    Role = adminCheck.Checked ? "admin" : "",
                    KeyPlain = lastKey,
                };
            }
            entries.Add(lastEntry);
            if (isRenew)
            {
                // 이전 항목 제거 (같은 ID 교체)
                var old = entries.FirstOrDefault(x => x.Id == id && x != lastEntry);
                if (old != null) entries.Remove(old);
            }
            SaveLocalKey(lastEntry);

            resultBox.Text = "=== 팀원에게 전달할 키 ===" + Environment.NewLine + lastKey
                           + Environment.NewLine + Environment.NewLine
                           + "Git 커밋 & 푸시 중...";
            copyKeyButton.Enabled = true;
            resultBox.Visible = true;

            string commitMsg = (isRenew ? "renew license '" : "issue license '")
                             + id + "' for '" + owner + "'"
                             + (expiry.Length > 0 ? " until " + expiry : " (no expiry)");
            bool pushOk = await SaveAndPush(commitMsg);

            if (isRenew)
            {
                renewTarget = null;
                idBox.ReadOnly = false;
                generateButton.Text = "키 생성 & 저장소 반영";
                cancelRenewButton.Enabled = false;
                cancelRenewButton.Visible = false;
            }

            resultBox.Text = resultBox.Text.Replace(
                "Git 커밋 & 푸시 중...",
                pushOk ? "[OK] Git 커밋 & 푸시 완료." : "[FAIL] Git 처리 실패 - 수동 확인 필요.");
            Reload();

            generateButton.Enabled = true;
            generateButton.Text = "키 생성 & 저장소 반영";
        }

        // ==================== JSON 직렬화 / 역직렬화 ====================

        private LicenseEntry ParseEntry(Dictionary<string, object> obj)
        {
            return new LicenseEntry
            {
                Id = Str(obj, "id"),
                Hash = Str(obj, "hash"),
                Owner = Str(obj, "owner"),
                ExpiresAt = Str(obj, "expiresAt"),
                CreatedAt = Str(obj, "createdAt"),
                Role = Str(obj, "role") == "admin" ? "admin" : "",   // 생략/오타 값은 일반 사용자
            };
        }

        private string SerializeManifest(List<LicenseEntry> allEntries, List<string> revokedIds)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"version\": 1,");
            sb.AppendLine("  \"keys\": [");
            for (int i = 0; i < allEntries.Count; i++)
            {
                var en = allEntries[i];
                var fields = new List<string>
                {
                    "\"id\": \"" + Escape(en.Id) + "\"",
                    "\"hash\": \"" + Escape(en.Hash) + "\"",
                    "\"owner\": \"" + Escape(en.Owner) + "\"",
                };
                if (en.ExpiresAt.Length > 0)
                    fields.Add("\"expiresAt\": \"" + Escape(en.ExpiresAt) + "\"");
                if (en.CreatedAt.Length > 0)
                    fields.Add("\"createdAt\": \"" + Escape(en.CreatedAt) + "\"");
                if (en.Role == "admin")
                    fields.Add("\"role\": \"admin\"");
                sb.Append("    { ");
                sb.Append(string.Join(", ", fields));
                sb.AppendLine(i < allEntries.Count - 1 ? " }," : " }");
            }
            sb.Append("  ]");
            if (revokedIds.Count > 0)
            {
                sb.AppendLine(",");
                sb.Append("  \"revoked\": [");
                sb.Append(string.Join(", ", revokedIds.Select(r => "\"" + Escape(r) + "\"").ToArray()));
                sb.Append("]");
            }
            sb.AppendLine();
            sb.Append("}");
            return sb.ToString();
        }

        // ---- 미니 JSON 파서 ----

        private static void SkipWs(string s, ref int p)
        {
            while (p < s.Length && char.IsWhiteSpace(s[p])) p++;
        }

        private Dictionary<string, object> ParseObject(string s, ref int p)
        {
            var dict = new Dictionary<string, object>();
            SkipWs(s, ref p);
            p++; // skip '{'
            SkipWs(s, ref p);
            if (p < s.Length && s[p] == '}') { p++; return dict; }
            while (true)
            {
                SkipWs(s, ref p);
                string key = ParseString(s, ref p);
                SkipWs(s, ref p);
                p++; // skip ':'
                SkipWs(s, ref p);
                object val = ParseValue(s, ref p);
                dict[key] = val;
                SkipWs(s, ref p);
                if (p < s.Length && s[p] == ',') { p++; continue; }
                if (p < s.Length && s[p] == '}') { p++; break; }
                break;
            }
            return dict;
        }

        private List<object> ParseArray(string s, ref int p)
        {
            var list = new List<object>();
            SkipWs(s, ref p);
            p++; // skip '['
            SkipWs(s, ref p);
            if (p < s.Length && s[p] == ']') { p++; return list; }
            while (true)
            {
                SkipWs(s, ref p);
                list.Add(ParseValue(s, ref p));
                SkipWs(s, ref p);
                if (p < s.Length && s[p] == ',') { p++; continue; }
                if (p < s.Length && s[p] == ']') { p++; break; }
                break;
            }
            return list;
        }

        private object ParseValue(string s, ref int p)
        {
            SkipWs(s, ref p);
            if (p >= s.Length) return null;
            char c = s[p];
            if (c == '"') return ParseString(s, ref p);
            if (c == '{') return ParseObject(s, ref p);
            if (c == '[') return ParseArray(s, ref p);
            int start = p;
            while (p < s.Length && ",}] \t\r\n".IndexOf(s[p]) < 0) p++;
            string lit = s.Substring(start, p - start);
            if (lit == "true") return (object)true;
            if (lit == "false") return (object)false;
            if (lit == "null") return null;
            double num;
            if (double.TryParse(lit, out num)) return num;
            return lit;
        }

        private string ParseString(string s, ref int p)
        {
            SkipWs(s, ref p);
            p++; // skip opening quote
            var sb = new StringBuilder();
            while (p < s.Length && s[p] != '"')
            {
                if (s[p] == '\\' && p + 1 < s.Length)
                {
                    p++;
                    switch (s[p])
                    {
                        case 'n': sb.Append('\n'); break;
                        case 't': sb.Append('\t'); break;
                        case 'r': sb.Append('\r'); break;
                        case '"': sb.Append('"'); break;
                        case '\\': sb.Append('\\'); break;
                        default: sb.Append(s[p]); break;
                    }
                }
                else sb.Append(s[p]);
                p++;
            }
            p++; // skip closing quote
            return sb.ToString();
        }

        private static string Str(Dictionary<string, object> d, string key)
        {
            return d.ContainsKey(key) && d[key] != null ? d[key].ToString() : "";
        }

        private static string Escape(string s)
        {
            if (s == null) return "";
            return s.Replace("\\", "\\\\").Replace("\"", "\\\"");
        }

        // ==================== 파일 쓰기 & Git push ====================

        private async System.Threading.Tasks.Task<bool> SaveAndPush(string commitMessage)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(JsonPath));
                File.WriteAllText(JsonPath, SerializeManifest(entries, revoked) + "\n", Encoding.UTF8);

                string repoDir = Path.GetDirectoryName(JsonPath);

                RunGit(repoDir, "add licenses.json");
                int staged = RunGitCapture(repoDir, "diff --cached --quiet");
                if (staged == 0)
                {
                    MessageBox.Show("변경사항이 없어 커밋하지 않았습니다.", "정보",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return true;
                }
                var gitLog = new StringBuilder();
                RunGitCapture(repoDir, "commit -m \"chore: " + commitMessage.Replace("\"", "") + "\"", gitLog);
                int pushExit = RunGitCapture(repoDir, "push origin main", gitLog);

                if (pushExit != 0)
                {
                    MessageBox.Show("Git push 실패. 커밋은 로컬에 남아 있습니다.\n\n=== Git 출력 ===\n" + gitLog.ToString(),
                        "푸시 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                MessageBox.Show("오류: " + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
            }
        }

        private void RunGit(string workdir, string args)
        {
            RunGitCapture(workdir, args, null);
        }

        private int RunGitCapture(string workdir, string args)
        {
            return RunGitCapture(workdir, args, null);
        }

        private int RunGitCapture(string workdir, string args, StringBuilder outputCapture)
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = workdir,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                // GCM이 터미널 없이도 인증을 시도할 수 있도록 환경 변수 전달
            };
            psi.EnvironmentVariables["GIT_TERMINAL_PROMPT"] = "0";
            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                var outTask = proc.StandardOutput.ReadToEndAsync();
                var errTask = proc.StandardError.ReadToEndAsync();
                if (!proc.WaitForExit(60000))
                {
                    try { proc.Kill(); } catch { }
                    if (outputCapture != null)
                        outputCapture.AppendLine("[timeout] git " + args);
                    return -1;
                }
                string stdout = outTask.Result;
                string stderr = errTask.Result;
                if (outputCapture != null)
                {
                    if (stdout.Trim().Length > 0) outputCapture.AppendLine(stdout.Trim());
                    if (stderr.Trim().Length > 0) outputCapture.AppendLine(stderr.Trim());
                }
                return proc.ExitCode;
            }
        }

        [STAThread]
        public static void Main()
        {
            string[] launchArgs = Environment.GetCommandLineArgs();
            if (launchArgs.Length > 1 && launchArgs[1] == "--test")
            {
                Environment.Exit(0);
            }
            Application.EnableVisualStyles();
            Application.Run(new MainForm());
        }
    }
}
