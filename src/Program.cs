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
    }

    public class MainForm : Form
    {
        private static readonly string ExeDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string JsonPath = Path.Combine(ExeDir, "licenses.json");
        public static string TestJsonPath { get { return JsonPath; } }

        private TabControl tabs;

        // 발급 현황 탭
        private ListView licenseList;
        private Button refreshButton, deleteButton, revokeButton, restoreButton;

        // 키 발급 탭
        private TextBox idBox, ownerBox, expiryBox, resultBox;
        private Button generateButton, copyKeyButton, copyJsonButton;
        private string lastKey = "";
        private LicenseEntry lastEntry = null;

        private List<LicenseEntry> entries = new List<LicenseEntry>();
        private List<string> revoked = new List<string>();

        public MainForm()
        {
            Text = "KJ FlowForge - 라이선스 관리";
            Size = new Size(760, 560);
            MinimumSize = new Size(700, 520);
            StartPosition = FormStartPosition.CenterScreen;

            tabs = new TabControl { Dock = DockStyle.Fill };
            var issueTab = BuildIssueTab();
            var listTab = BuildListTab();
            tabs.TabPages.Add(listTab);
            tabs.TabPages.Add(issueTab);

            Controls.Add(tabs);
            Load += (s, e) => Reload();
        }

        // ==================== 발급 현황 탭 ====================

        private TabPage BuildListTab()
        {
            var page = new TabPage("발급 현황");

            var topPanel = new FlowLayoutPanel
            {
                Location = new Point(12, 12),
                Size = new Size(720, 42),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            refreshButton = new Button { Text = "새로고침", Width = 90, Height = 32 };
            refreshButton.Click += (s, e) => Reload();
            deleteButton = new Button { Text = "삭제", Width = 70, Height = 32, Enabled = false };
            deleteButton.Click += (s, e) => DeleteSelected();
            revokeButton = new Button { Text = "폐기", Width = 70, Height = 32, Enabled = false };
            revokeButton.Click += (s, e) => ToggleRevoke(true);
            restoreButton = new Button { Text = "복원", Width = 70, Height = 32, Enabled = false };
            restoreButton.Click += (s, e) => ToggleRevoke(false);
            topPanel.Controls.AddRange(new Control[] { refreshButton, deleteButton, revokeButton, restoreButton });

            licenseList = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                Location = new Point(12, 60),
                Size = new Size(720, 420),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font("Consolas", 9),
            };
            licenseList.Columns.Add("키 ID", 170);
            licenseList.Columns.Add("사용자", 140);
            licenseList.Columns.Add("만료일", 100);
            licenseList.Columns.Add("상태", 80);
            licenseList.Columns.Add("생성일", 100);
            licenseList.SelectedIndexChanged += OnListSelectionChanged;

            var hintLabel = new Label
            {
                Text = "※ 삭제/폐기/복원 시 자동으로 커밋 & 푸시됩니다.",
                Location = new Point(12, 490),
                AutoSize = true,
                ForeColor = Color.Gray,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };

            page.Controls.AddRange(new Control[] { topPanel, licenseList, hintLabel });
            return page;
        }

        private void OnListSelectionChanged(object sender, EventArgs e)
        {
            if (licenseList.SelectedItems.Count == 0)
            {
                deleteButton.Enabled = revokeButton.Enabled = restoreButton.Enabled = false;
                return;
            }
            var entry = GetSelected();
            bool isRevoked = entry != null && revoked.Contains(entry.Id);
            deleteButton.Enabled = true;
            revokeButton.Enabled = !isRevoked;
            restoreButton.Enabled = isRevoked;
        }

        private LicenseEntry GetSelected()
        {
            if (licenseList.SelectedItems.Count == 0) return null;
            string id = licenseList.SelectedItems[0].SubItems[0].Text;
            return entries.FirstOrDefault(x => x.Id == id);
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
            RenderList();
        }

        private void RenderList()
        {
            licenseList.Items.Clear();
            DateTime now = DateTime.Today;
            foreach (var en in entries)
            {
                string status;
                Color color = Color.Black;
                DateTime expDt;
                if (revoked.Contains(en.Id)) { status = "폐기됨"; color = Color.DarkRed; }
                else if (en.ExpiresAt.Length > 0 && DateTime.TryParse(en.ExpiresAt, out expDt))
                {
                    int daysLeft = (int)(expDt - now).TotalDays;
                    if (daysLeft < 0) { status = "만료"; color = Color.Red; }
                    else if (daysLeft <= 7) { status = "D-" + daysLeft; color = Color.Orange; }
                    else status = "유효";
                }
                else status = "무기한";

                var item = new ListViewItem(en.Id);
                item.SubItems.Add(en.Owner);
                item.SubItems.Add(en.ExpiresAt.Length > 0 ? en.ExpiresAt : "-");
                item.SubItems.Add(status);
                item.SubItems.Add(en.CreatedAt.Length > 0 ? en.CreatedAt : "-");
                item.ForeColor = color;
                licenseList.Items.Add(item);
            }
        }

        private async void DeleteSelected()
        {
            var entry = GetSelected();
            if (entry == null) return;
            var confirm = MessageBox.Show(
                "키 '" + entry.Id + "' (" + entry.Owner + ") 을/를 완전히 삭제할까요?\n이 작업은 커밋 & 푸시됩니다.",
                "삭제 확인", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
            if (confirm != DialogResult.Yes) return;
            entries.RemoveAll(x => x.Id == entry.Id);
            await SaveAndPush("remove license '" + entry.Id + "' (" + entry.Owner + ")");
            RenderList();
        }

        private async void ToggleRevoke(bool doRevoke)
        {
            var entry = GetSelected();
            if (entry == null) return;
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

            var label1 = new Label { Text = "키 ID (구분용, 영문 권장)", Location = new Point(20, 20), AutoSize = true };
            idBox = new TextBox { Location = new Point(20, 43), Width = 500 };

            var label2 = new Label { Text = "사용자 이름", Location = new Point(20, 75), AutoSize = true };
            ownerBox = new TextBox { Location = new Point(20, 98), Width = 500 };

            var label3 = new Label { Text = "만료일 (YYYY-MM-DD, 비우면 무기한)", Location = new Point(20, 130), AutoSize = true };
            expiryBox = new TextBox { Location = new Point(20, 153), Width = 500 };

            generateButton = new Button
            {
                Text = "키 생성 & 저장소 반영",
                Location = new Point(20, 190),
                Width = 180,
                Height = 36,
            };
            generateButton.Font = new Font(generateButton.Font, FontStyle.Bold);
            generateButton.Click += OnGenerate;

            copyKeyButton = new Button { Text = "키 복사", Location = new Point(210, 192), Width = 90, Height = 32, Enabled = false };
            copyKeyButton.Click += (s, e) => { if (lastKey.Length > 0) Clipboard.SetText(lastKey); };

            copyJsonButton = new Button { Text = "JSON 항목 복사", Location = new Point(310, 192), Width = 120, Height = 32, Enabled = false };
            copyJsonButton.Click += (s, e) =>
            {
                if (lastEntry != null) Clipboard.SetText(BuildEntryJson(lastEntry));
            };

            resultBox = new TextBox
            {
                Location = new Point(20, 240),
                Width = 500,
                Height = 220,
                Multiline = true,
                ReadOnly = true,
                ScrollBars = ScrollBars.Vertical,
                Font = new Font("Consolas", 9),
            };

            page.Controls.AddRange(new Control[] { label1, idBox, label2, ownerBox, label3, expiryBox,
                generateButton, copyKeyButton, copyJsonButton, resultBox });
            return page;
        }

        private async void OnGenerate(object sender, EventArgs e)
        {
            string id = idBox.Text.Trim();
            string owner = ownerBox.Text.Trim();
            string expiry = expiryBox.Text.Trim();

            if (id.Length == 0 || owner.Length == 0)
            {
                MessageBox.Show("키 ID와 사용자 이름을 모두 입력해 주세요.", "입력 필요",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            DateTime parsedExpiry;
            if (expiry.Length > 0 && !DateTime.TryParse(expiry, out parsedExpiry))
            {
                MessageBox.Show("만료일 형식을 YYYY-MM-DD 로 입력해 주세요.", "형식 오류",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
            }
            if (entries.Any(x => x.Id == id))
            {
                MessageBox.Show("'" + id + "' 는 이미 존재하는 키 ID입니다. 다른 ID를 입력해 주세요.", "중복",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
                return;
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
                };
            }
            entries.Add(lastEntry);

            resultBox.Text = "=== 팀원에게 전달할 키 ===" + Environment.NewLine + lastKey
                           + Environment.NewLine + Environment.NewLine
                           + "=== licenses.json 항목 ===" + Environment.NewLine
                           + BuildEntryJson(lastEntry) + Environment.NewLine + Environment.NewLine
                           + "Git 커밋 & 푸시 중...";
            copyKeyButton.Enabled = true;
            copyJsonButton.Enabled = true;

            string commitMsg = "issue license '" + id + "' for '" + owner + "'" +
                               (expiry.Length > 0 ? " until " + expiry : "");
            bool ok = await SaveAndPush(commitMsg);

            resultBox.Text = resultBox.Text.Replace(
                "Git 커밋 & 푸시 중...",
                ok ? "[OK] Git 커밋 & 푸시 완료." : "[FAIL] Git 처리 실패 - 수동 확인 필요.");
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
            };
        }

        private string BuildEntryJson(LicenseEntry en)
        {
            var parts = new List<string>
            {
                "  \"id\": \"" + Escape(en.Id) + "\"",
                "  \"hash\": \"" + Escape(en.Hash) + "\"",
                "  \"owner\": \"" + Escape(en.Owner) + "\"",
            };
            if (en.ExpiresAt.Length > 0)
                parts.Add("  \"expiresAt\": \"" + Escape(en.ExpiresAt) + "\"");
            if (en.CreatedAt.Length > 0)
                parts.Add("  \"createdAt\": \"" + Escape(en.CreatedAt) + "\"");
            return "{" + Environment.NewLine + string.Join("," + Environment.NewLine, parts) + Environment.NewLine + "}";
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
                RunGit(repoDir, "commit -m \"chore: " + commitMessage.Replace("\"", "") + "\"");
                int pushExit = RunGitCapture(repoDir, "push origin main");

                if (pushExit != 0)
                {
                    MessageBox.Show("Git push 실패. 네트워크/인증 상태를 확인해 주세요.\n커밋은 로컬에 남아 있습니다.",
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
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "git",
                Arguments = args,
                WorkingDirectory = workdir,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                proc.WaitForExit(30000);
            }
        }

        private int RunGitCapture(string workdir, string args)
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
            };
            using (var proc = System.Diagnostics.Process.Start(psi))
            {
                proc.WaitForExit(30000);
                return proc.ExitCode;
            }
        }

        [STAThread]
        public static void Main()
        {
            if (Environment.GetCommandLineArgs().Length > 1 && Environment.GetCommandLineArgs()[1] == "--test")
            {
                Environment.Exit(TestHarness.Run());
            }
            Application.EnableVisualStyles();
            Application.Run(new MainForm());
        }
    }
}
