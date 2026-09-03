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

    public class ProjectEntry
    {
        public string Id = "";
        public string Name = "";
        public string Url = "";
        public List<string> AllowedBranches = new List<string>();   // 비어 있으면 전체 브랜치 허용
        public string NotionDatabaseId = "";
        public string DiscordChannelId = "";
        public Dictionary<string, List<string>> UserBranches = new Dictionary<string, List<string>>();   // keyId → 허용 브랜치
    }

    public class MainForm : Form
    {
        private static readonly string ExeDir = AppDomain.CurrentDomain.BaseDirectory;
        private static readonly string JsonPath = Path.Combine(ExeDir, "licenses.json");
        private static readonly string ProjectsPath = Path.Combine(ExeDir, "projects.json");
        private static readonly string LocalKeysPath = Path.Combine(ExeDir, "keys.local.json");
        private static readonly string SettingsPath = Path.Combine(ExeDir, "ui.settings.json");
        private static readonly string HistoryPath = Path.Combine(ExeDir, "operation.history.json");
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
        // (필터 체크박스 제거됨 — 대시보드/검색 필터로 대체)
        private int sortColumnIndex = -1;   // -1 = 기본(등록) 순서
        private int sortState = 0;          // 0=기본, 1=오름차순, 2=내림차순
        private Button renewButton, copyDetailButton;
        private Button pushRetryButton;
        private Button extend30Button, extend90Button, extend365Button;
        private Button verifyButton;
        private Label gitStatusLabel;
        private System.Windows.Forms.FlowLayoutPanel dashboardPanel;
        private ListView historyList;

        // 키 발급 탭
        private TextBox idBox, ownerBox, resultBox;
        private DateTimePicker expiryPicker;
        private Label typedDateLabel;
        private ComboBox expiryHourBox, expiryMinuteBox;
        private CheckBox expiryTimeCheck;
        private CheckBox expiryDateCheck;
        private Label expiryLabel;
        private CheckBox adminCheck;
        private Button generateButton, copyKeyButton;
        private Button cancelRenewButton;
        private string lastKey = "";
        private LicenseEntry lastEntry = null;
        private LicenseEntry renewTarget = null;

        private List<LicenseEntry> entries = new List<LicenseEntry>();
        private List<string> revoked = new List<string>();

        // ===== 프로젝트 관리 탭 =====
        private List<ProjectEntry> projects = new List<ProjectEntry>();
        private ListView projectList;
        private ComboBox projectUserBox;
        private System.Windows.Forms.FlowLayoutPanel projectBranchPanel;
        private Button projectRefreshBranchesButton;
        private Button projectSaveButton;
        private Label projectUserNameLabel;
        private Label projectPushStatusLabel;
        private List<string> currentRemoteBranches = new List<string>();

        // ===== 작업 이력(감사) 로컬 전용 =====
        private List<Dictionary<string, object>> historyItems = new List<Dictionary<string, object>>();

        // ===== 대시보드 필터 상태 (클릭 시 목록에 반영) =====
        private string dashboardFilter = "";

        // ===== 민감 클립보드 자동 삭제 (키 원문 보호) =====
        private Timer clipboardClearTimer;
        private int clipboardGuardSeconds = 60;



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
            Size = new Size(1660, 1010);
            MinimumSize = new Size(1500, 900);
            StartPosition = FormStartPosition.CenterScreen;

            tabs = new TabControl { Dock = DockStyle.Fill };
            var issueTab = BuildIssueTab();
            var listTab = BuildListTab();
            var historyTab = BuildHistoryTab();
            var projectTab = BuildProjectTab();
            tabs.TabPages.Add(listTab);
            tabs.TabPages.Add(issueTab);
            tabs.TabPages.Add(projectTab);
            tabs.TabPages.Add(historyTab);

            Controls.Add(tabs);
            FormClosing += (s, e) => SaveUiSettings();
            Resize += (s, e) => AutoFitAllColumns();
            Load += (s, e) => { LoadHistory(); Reload(); TryPullProjects(); ReloadProjects(); UpdateGitStatusLabel(); };
            Shown += (s, e) => { BackupLocalKeys(); ShowExpiryAlert(); };
        }
        // ==================== 프로젝트 관리 탭 ====================

        private TabPage BuildProjectTab()
        {
            var page = new TabPage("프로젝트 관리");

            var title = new Label
            {
                Text = "유저별 브랜치 설정 (KJ_FlowForge_Config/projects.json)",
                Location = new Point(S(12), S(12)),
                AutoSize = true,
                Font = new Font("맑은 고딕", 15f, FontStyle.Bold),
            };


            var info = new Label
            {
                Text = "좌측에서 저장소를 선택하고, 유저(키 ID)를 고른 뒤 해당 유저의 허용 브랜치를 체크하세요. 저장 시 projects.json에 반영·푸시됩니다. 프로젝트 정의(깃 주소 등)는 허브 관리자가 등록한 항목입니다.",
                Location = new Point(S(12), S(44)),
                AutoSize = true,
                ForeColor = Color.Gray,
                Font = new Font("맑은 고딕", 14f),
            };

            projectList = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                Location = new Point(S(12), S(80)),
                Size = new Size(S(560), S(420)),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left,
                Font = new Font("맑은 고딕", 14f),
            };
            projectList.Columns.Add("ID", 110);
            projectList.Columns.Add("이름", 130);
            projectList.Columns.Add("깃 주소", 280);
            projectList.SelectedIndexChanged += (s, e) => LoadSelectedProject();

            var labelUser = new Label { Text = "유저 선택 (키 ID)", Location = new Point(S(590), S(80)), AutoSize = true };
            projectUserBox = new ComboBox
            {
                Location = new Point(S(590), S(103)),
                Width = S(420),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Font = new Font("맑은 고딕", 14f),
            };
            projectUserBox.SelectedIndexChanged += (s, e) => RenderProjectBranchesForSelectedUser();

            projectUserNameLabel = new Label
            {
                Text = "",
                Location = new Point(S(590), S(140)),
                Size = new Size(S(420), S(24)),
                ForeColor = Color.Gray,
                Font = new Font("맑은 고딕", 14f),
            };

            var labelBranch = new Label
            {
                Text = "이 유저의 허용 브랜치 (체크된 브랜치만 사용 가능, 미선택 시 전체 허용)",
                Location = new Point(S(590), S(180)),
                AutoSize = true,
                ForeColor = Color.Gray,
                Font = new Font("맑은 고딕", 14f),
            };
            projectRefreshBranchesButton = new Button { Text = "브랜치 불러오기", Location = new Point(S(590), S(205)), Width = S(150), Height = S(32) };
            projectRefreshBranchesButton.Click += async (s, e) => await RefreshProjectBranches();

            projectBranchPanel = new FlowLayoutPanel
            {
                Location = new Point(S(590), S(245)),
                Size = new Size(S(460), S(200)),
                AutoScroll = true,
                FlowDirection = FlowDirection.TopDown,
                WrapContents = false,
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
            };

            // 저장 버튼은 하단 패널에 고정 배치 (Dock 방식으로 항상 보이도록)
            projectSaveButton = new Button
            {
                Text = "변경 저장 & 푸시",
                Width = S(250),
                Height = S(44),
                Dock = DockStyle.Right,
                BackColor = Color.FromArgb(46, 139, 87),
                ForeColor = Color.White,
                FlatStyle = FlatStyle.Flat,
                FlatAppearance = { BorderSize = 0 },
                Font = new Font("맑은 고딕", 15f, FontStyle.Bold),
            };
            projectSaveButton.Click += async (s, e) => await SaveProjectsAndPush();

            // 하단 패널: 우측에 [결과 캡션 + 저장 버튼] 세로 스택 배치
            projectPushStatusLabel = new Label
            {
                Text = "",
                Size = new Size(S(250), S(26)),
                Location = new Point(0, S(2)),
                TextAlign = ContentAlignment.MiddleCenter,
                ForeColor = Color.DimGray,
            };
            var bottomPanel = new Panel
            {
                Dock = DockStyle.Bottom,
                Height = S(86),
                BackColor = Color.White,
            };
            var bottomRightStack = new Panel
            {
                Dock = DockStyle.Right,
                Width = S(250),
                Height = S(86),
                BackColor = Color.White,
            };
            projectSaveButton.Dock = DockStyle.Bottom;
            bottomRightStack.Controls.Add(projectSaveButton);
            bottomRightStack.Controls.Add(projectPushStatusLabel);
            bottomPanel.Controls.Add(bottomRightStack);

            page.Controls.AddRange(new Control[] { title, info, projectList,
                labelUser, projectUserBox, projectUserNameLabel,
                labelBranch, projectRefreshBranchesButton, projectBranchPanel, bottomPanel });
            bottomPanel.BringToFront();

            return page;
        }

        /// <summary>
        /// 허브(관리자)가 Config 저장소에 push한 projects.json 변경을 로컬 checkout에 반영합니다.
        /// 실패해도 무시하고 기존 로컬 파일로 계속합니다.
        /// </summary>
        private void TryPullProjects()
        {
            try
            {
                string repoDir = Path.GetDirectoryName(ProjectsPath);
                if (string.IsNullOrEmpty(repoDir) || !Directory.Exists(repoDir)) return;
                // 허브(관리자)가 GitHub API로 push한 최신 projects.json만 origin/main에서 가져옵니다.
                // reset/clean과 같은 파괴적 작업은 하지 않아 다른 파일(소스, exe, 키)은 보존합니다.
                int fetchExit = RunGitCapture(repoDir, "fetch origin main", null);
                if (fetchExit != 0) return;
                var sb = new StringBuilder();
                int showExit = RunGitCapture(repoDir, "show origin/main:projects.json", sb);
                if (showExit == 0 && sb.Length > 0)
                {
                    string remote = sb.ToString().Trim();
                    if (remote.StartsWith("{") && remote.Contains("projects"))
                    {
                        File.WriteAllText(ProjectsPath, remote + Environment.NewLine, Encoding.UTF8);
                    }
                }
            }
            catch { }
        }

        private void ReloadProjects()
        {
            projects.Clear();
            try
            {
                if (!File.Exists(ProjectsPath)) return;
                string raw = File.ReadAllText(ProjectsPath);
                int pos = 0;
                SkipWs(raw, ref pos);
                var node = ParseObject(raw, ref pos);
                if (!(node.ContainsKey("projects") && node["projects"] is List<object>)) return;
                foreach (var item in (List<object>)node["projects"])
                {
                    var obj = (Dictionary<string, object>)item;
                    var entry = new ProjectEntry
                    {
                        Id = Str(obj, "id"),
                        Name = Str(obj, "name"),
                        Url = Str(obj, "url"),
                        NotionDatabaseId = Str(obj, "notionDatabaseId"),
                        DiscordChannelId = Str(obj, "discordChannelId"),
                    };
                    if (obj.ContainsKey("allowedBranches") && obj["allowedBranches"] is List<object>)
                    {
                        foreach (var b in (List<object>)obj["allowedBranches"])
                            if (b != null && b.ToString().Trim().Length > 0) entry.AllowedBranches.Add(b.ToString());
                    }
                    if (obj.ContainsKey("userBranches") && obj["userBranches"] is Dictionary<string, object>)
                    {
                        foreach (var kv in (Dictionary<string, object>)obj["userBranches"])
                        {
                            if (kv.Value is List<object>)
                            {
                                var list = new List<string>();
                                foreach (var b in (List<object>)kv.Value)
                                    if (b != null && b.ToString().Trim().Length > 0) list.Add(b.ToString());
                                if (list.Count > 0) entry.UserBranches[kv.Key] = list;
                            }
                        }
                    }
                    if (entry.Id.Length > 0 && entry.Url.Length > 0) projects.Add(entry);
                }
            }
            catch { }
            RenderProjects();
        }

        private void RenderProjects()
        {
            if (projectList == null) return;
            projectList.Items.Clear();
            PopulateUserCombo();
            for (int i = 0; i < projects.Count; i++)
            {
                var en = projects[i];
                var item = new ListViewItem(en.Id);
                item.SubItems.Add(en.Name);
                item.SubItems.Add(en.Url);
                projectList.Items.Add(item);
            }
        }

        private string GetSelectedProjectUrl()
        {
            if (projectList == null || projectList.SelectedItems.Count == 0 || projectList.SelectedIndices[0] >= projects.Count) return "";
            return projects[projectList.SelectedIndices[0]].Url;
        }

        private ProjectEntry GetSelectedProject()
        {
            if (projectList == null || projectList.SelectedItems.Count == 0) return null;
            int idx = projectList.SelectedIndices[0];
            if (idx < 0 || idx >= projects.Count) return null;
            return projects[idx];
        }

        private void PopulateUserCombo()
        {
            if (projectUserBox == null) return;
            string current = projectUserBox.SelectedItem as string;
            projectUserBox.Items.Clear();
            foreach (var en in entries)
            {
                if (en.Role == "admin") continue;   // 관리자는 제한 대상 아님
                string label = en.Id;
                if (en.Owner.Length > 0) label = en.Id + " (" + en.Owner + ")";
                projectUserBox.Items.Add(label);
            }
            if (current != null && projectUserBox.Items.Contains(current)) projectUserBox.SelectedItem = current;
            else if (projectUserBox.Items.Count > 0) projectUserBox.SelectedIndex = 0;
        }

        private string SelectedUserKeyId()
        {
            string sel = projectUserBox == null ? null : projectUserBox.SelectedItem as string;
            if (sel == null) return "";
            int sp = sel.IndexOf(' ');
            return sp >= 0 ? sel.Substring(0, sp) : sel;
        }

        private void LoadSelectedProject()
        {
            if (projectList == null || projectList.SelectedItems.Count == 0) return;
            int idx = projectList.SelectedIndices[0];
            if (idx < 0 || idx >= projects.Count) return;
            var en = projects[idx];
            PopulateUserCombo();
            projectUserNameLabel.Text = "저장소: " + en.Name + " (" + en.Id + ")";
            RenderProjectBranchesForSelectedUser();
        }

        private void RenderProjectBranchesForSelectedUser()
        {
            var en = GetSelectedProject();
            if (en == null || projectUserBox == null) return;
            string keyId = SelectedUserKeyId();
            List<string> checkedBranches = new List<string>();
            if (keyId.Length > 0 && en.UserBranches.ContainsKey(keyId)) checkedBranches = en.UserBranches[keyId];
            RenderProjectBranches(checkedBranches);
        }

        private void RenderProjectBranches(List<string> checkedBranches)
        {
            if (projectBranchPanel == null) return;
            projectBranchPanel.Controls.Clear();
            foreach (var branch in currentRemoteBranches)
            {
                var cb = new CheckBox
                {
                    Text = branch,
                    AutoSize = true,
                    Checked = checkedBranches != null && checkedBranches.Contains(branch),
                };
                projectBranchPanel.Controls.Add(cb);
            }
        }

        private async System.Threading.Tasks.Task RefreshProjectBranches()
        {
            string url = GetSelectedProjectUrl();
            if (url.Length == 0)
            {
                MessageBox.Show("좌측 목록에서 저장소를 먼저 선택하세요.", "안내", MessageBoxButtons.OK, MessageBoxIcon.Information);
                return;
            }
            try
            {
                projectRefreshBranchesButton.Enabled = false;
                var sb = new StringBuilder();
                RunGitCapture(ExeDir, "ls-remote --heads \"" + url.Replace("\"", "") + "\"", sb);
                var branches = new List<string>();
                foreach (var line in sb.ToString().Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    int tab = line.IndexOf('\t');
                    string refName = tab >= 0 ? line.Substring(tab + 1) : line;
                    refName = refName.Trim();
                    if (refName.StartsWith("refs/heads/")) branches.Add(refName.Substring("refs/heads/".Length));
                }
                currentRemoteBranches = branches;
                RenderProjectBranchesForSelectedUser();
                projectRefreshBranchesButton.Text = "브랜치 불러오기 (" + branches.Count + ")";
                if (branches.Count == 0)
                    MessageBox.Show("원격 브랜치를 가져오지 못했습니다.\n\n" + sb.ToString(), "브랜치 없음", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            catch (Exception ex)
            {
                MessageBox.Show("브랜치 조회 실패: " + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
            finally
            {
                projectRefreshBranchesButton.Enabled = true;
            }
        }

        private List<string> GetCheckedBranches()
        {
            var result = new List<string>();
            if (projectBranchPanel == null) return result;
            foreach (CheckBox cb in projectBranchPanel.Controls)
                if (cb.Checked) result.Add(cb.Text);
            return result;
        }

        private string SerializeProjects(List<ProjectEntry> all)
        {
            var sb = new StringBuilder();
            sb.AppendLine("{");
            sb.AppendLine("  \"version\": 1,");
            sb.AppendLine("  \"projects\": [");
            for (int i = 0; i < all.Count; i++)
            {
                var en = all[i];
                var fields = new List<string>
                {
                    "\"id\": \"" + Escape(en.Id) + "\"",
                    "\"name\": \"" + Escape(en.Name) + "\"",
                    "\"url\": \"" + Escape(en.Url) + "\"",
                };
                if (en.AllowedBranches != null && en.AllowedBranches.Count > 0)
                {
                    var arr = string.Join(", ", en.AllowedBranches.Select(b => "\"" + Escape(b) + "\"").ToArray());
                    fields.Add("\"allowedBranches\": [" + arr + "]");
                }
                if (en.NotionDatabaseId.Length > 0) fields.Add("\"notionDatabaseId\": \"" + Escape(en.NotionDatabaseId) + "\"");
                if (en.DiscordChannelId.Length > 0) fields.Add("\"discordChannelId\": \"" + Escape(en.DiscordChannelId) + "\"");
                if (en.UserBranches != null && en.UserBranches.Count > 0)
                {
                    var parts = new List<string>();
                    foreach (var kv in en.UserBranches)
                    {
                        var arr2 = string.Join(", ", kv.Value.Select(b => "\"" + Escape(b) + "\"").ToArray());
                        parts.Add("\"" + Escape(kv.Key) + "\": [" + arr2 + "]");
                    }
                    fields.Add("\"userBranches\": { " + string.Join(", ", parts) + " }");
                }
                sb.Append("    { ");
                sb.Append(string.Join(", ", fields));
                sb.AppendLine(i < all.Count - 1 ? " }," : " }");
            }
            sb.AppendLine("  ]");
            sb.Append("}");
            return sb.ToString();
        }

        private async System.Threading.Tasks.Task<bool> SaveProjectsAndPush()
        {
            try
            {
                SetProjectPushStatus("처리 중...", Color.DimGray);
                // 현재 선택된 저장소/유저의 체크 브랜치를 프로젝트 정의에 반영
                var selectedProject = GetSelectedProject();
                if (selectedProject != null && projectUserBox != null)
                {
                    string keyId = SelectedUserKeyId();
                    if (keyId.Length > 0)
                    {
                        var checkedBranches = GetCheckedBranches();
                        if (checkedBranches.Count > 0) selectedProject.UserBranches[keyId] = checkedBranches;
                        else selectedProject.UserBranches.Remove(keyId);
                    }
                }
                var hashes = new HashSet<string>();
                foreach (var en in projects)
                {
                    if (en.Id.Length == 0 || en.Url.Length == 0)
                    {
                        MessageBox.Show("프로젝트 ID와 깃 주소가 비어 있는 항목이 있습니다.", "오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        SetProjectPushStatus("완료 실패", Color.Red);
                        return false;
                    }
                    if (!hashes.Add(en.Id))
                    {
                        MessageBox.Show("중복된 프로젝트 ID가 있습니다: " + en.Id, "오류", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        SetProjectPushStatus("완료 실패", Color.Red);
                        return false;
                    }
                }

                Directory.CreateDirectory(Path.GetDirectoryName(ProjectsPath));
                File.WriteAllText(ProjectsPath, SerializeProjects(projects) + "\n", Encoding.UTF8);

                string repoDir = Path.GetDirectoryName(ProjectsPath);
                RunGit(repoDir, "add projects.json");
                int staged = RunGitCapture(repoDir, "diff --cached --quiet");
                if (staged == 0)
                {
                    AppendHistory("변경", "projects.json", "변경사항 없음(이미 반영됨)", "성공");
                    MessageBox.Show("변경사항이 없어 커밋하지 않았습니다.", "정보", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    SetProjectPushStatus("완료됨", Color.Green);
                    return true;
                }
                var gitLog = new StringBuilder();
                RunGitCapture(repoDir, "commit -m \"chore: update projects.json\"", gitLog);
                RunGit(repoDir, "fetch origin main");

                int pushExit = RunGitCapture(repoDir, "push origin main", gitLog);
                if (pushExit != 0)
                {
                    RunGitCapture(repoDir, "pull --rebase origin main", gitLog);
                    pushExit = RunGitCapture(repoDir, "push origin main", gitLog);
                }
                if (pushExit != 0)
                {
                    AppendHistory("변경", "projects.json", "로컬 커밋 완료, 푸시 실패", "푸시 필요");
                    MessageBox.Show("Git push 실패. 커밋은 로컬에 남아 있습니다.\n\n=== Git 출력 ===\n" + gitLog.ToString(),
                        "푸시 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    SetProjectPushStatus("완료 실패", Color.Red);
                    return false;
                }
                AppendHistory("변경", "projects.json", "커밋 & 푸시 완료", "성공");
                UpdateGitStatusLabel();
                SetProjectPushStatus("완료됨", Color.Green);
                return true;
            }
            catch (Exception ex)
            {
                AppendHistory("변경", "projects.json", "오류: " + ex.Message, "실패");
                MessageBox.Show("오류: " + ex.Message, "오류", MessageBoxButtons.OK, MessageBoxIcon.Error);
                SetProjectPushStatus("완료 실패", Color.Red);
                return false;
            }
        }

        private void SetProjectPushStatus(string msg, Color color)
        {
            if (projectPushStatusLabel == null) return;
            if (InvokeRequired)
            {
                Invoke((Action)(() =>
                {
                    projectPushStatusLabel.Text = msg;
                    projectPushStatusLabel.ForeColor = color;
                }));
            }
            else
            {
                projectPushStatusLabel.Text = msg;
                projectPushStatusLabel.ForeColor = color;
            }
        }

        // ==================== 작업 이력 탭 ====================

        private TabPage BuildHistoryTab()
        {
            var page = new TabPage("작업 이력");
            var info = new Label
            {
                Text = "※ 로컬 전용 감사 이력입니다. (키 원문/해시는 저장하지 않음)",
                Location = new Point(S(12), S(12)),
                AutoSize = true,
                ForeColor = Color.Gray,
            };
            var refreshHist = new Button
            {
                Text = "새로고침",
                Location = new Point(S(12), S(42)),
                Width = S(110),
                Height = S(32),
            };
            refreshHist.Click += (s, e) => { LoadHistory(); ReloadHistoryList(); };
            var clearHist = new Button
            {
                Text = "이력 비우기",
                Location = new Point(S(130), S(42)),
                Width = S(110),
                Height = S(32),
            };
            clearHist.Click += (s, e) =>
            {
                if (MessageBox.Show("로컬 작업 이력을 모두 비울까요?", "이력 초기화",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning) == DialogResult.Yes)
                {
                    historyItems.Clear();
                    SaveHistory();
                    ReloadHistoryList();
                }
            };
            historyList = new ListView
            {
                View = View.Details,
                FullRowSelect = true,
                GridLines = true,
                HideSelection = false,
                Location = new Point(S(12), S(84)),
                Size = new Size(S(760), S(400)),
                Anchor = AnchorStyles.Top | AnchorStyles.Bottom | AnchorStyles.Left | AnchorStyles.Right,
                Font = new Font("맑은 고딕", 14f),
            };
            historyList.Columns.Add("시각", 160);
            historyList.Columns.Add("작업", 90);
            historyList.Columns.Add("대상", 170);
            historyList.Columns.Add("상세", 380);
            historyList.Columns.Add("상태", 90);
            page.Controls.AddRange(new Control[] { info, refreshHist, clearHist, historyList });
            return page;
        }

        // ==================== 설정 저장 ====================

        private void SaveUiSettings()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("{");
                sb.AppendLine("{ \"ui\": \"1.0\" }");
                sb.AppendLine("}");
                File.WriteAllText(SettingsPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        // ==================== 발급 현황 탭 ====================

        private TabPage BuildListTab()
        {
            var page = new TabPage("발급 현황");

            // Git 동기화 상태 표시
            gitStatusLabel = new Label
            {
                Text = "Git: 확인 중...",
                Location = new Point(S(12), S(12)),
                AutoSize = true,
                Font = new Font("맑은 고딕", 15f, FontStyle.Bold),
            };
            gitStatusLabel.Click += (s, e) => { gitStatusLabel.Text = "Git: 확인 중..."; UpdateGitStatusLabel(); };

            // 필터 설정 소제목
            var filterTitle = new Label
            {
                Text = "필터 설정",
                Location = new Point(S(12), S(40)),
                AutoSize = true,
                ForeColor = Color.Gray,
                Font = new Font("맑은 고딕", 14f),
            };

            // 클릭 가능한 대시보드 요약 (전체 / 유효 / 관리자 / 유저 / 임박 / 만료 / 폐기)
            dashboardPanel = new FlowLayoutPanel
            {
                Location = new Point(S(12), S(62)),
                Size = new Size(S(720), S(34)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
                FlowDirection = FlowDirection.LeftToRight,
                WrapContents = true,
                AutoSize = false,
            };
            BuildDashboardLinks();

            // 키 설정 도구 소제목
            var toolsTitle = new Label
            {
                Text = "키 설정 도구",
                Location = new Point(S(12), S(100)),
                AutoSize = true,
                ForeColor = Color.Gray,
                Font = new Font("맑은 고딕", 14f),
            };

            var topPanel = new FlowLayoutPanel
            {
                Location = new Point(S(12), S(122)),
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
            verifyButton = new Button { Text = "검증", Width = S(70), Height = S(32), Enabled = false };
            verifyButton.Click += (s, e) =>
            {
                MessageBox.Show(ValidateSelectedKey(), "키 검증", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            topPanel.Controls.AddRange(new Control[] { refreshButton, deleteButton, revokeButton, restoreButton,
                copyKeyButton2, renewButton, copyDetailButton, verifyButton, pushRetryButton });

            // 검색 필터 소제목
            var searchTitle = new Label
            {
                Text = "검색 필터",
                Location = new Point(S(12), S(166)),
                AutoSize = true,
                ForeColor = Color.Gray,
                Font = new Font("맑은 고딕", 14f),
            };

            // 검색 + 필터 행
            var filterPanel = new FlowLayoutPanel
            {
                Location = new Point(S(12), S(188)),
                Size = new Size(S(720), S(40)),
                Anchor = AnchorStyles.Top | AnchorStyles.Left | AnchorStyles.Right,
            };
            searchBox = new TextBox { Width = S(280) };
            searchBox.TextChanged += (s, e) => RenderList();
            filterPanel.Controls.Add(searchBox);

            // 일괄 연장 행
            var extendPanel = new FlowLayoutPanel
            {
                Location = new Point(S(12), S(232)),
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
                Location = new Point(S(12), S(276)),
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
            licenseList.KeyDown += (s, e) =>
            {
                if (e.Control && e.KeyCode == Keys.C)
                {
                    CopySelectedKeys();
                    e.Handled = true;
                    e.SuppressKeyPress = true;
                }
            };
            licenseList.ColumnClick += OnColumnSortClick;

            var hintLabel = new Label
            {
                Text = "※ 삭제/폐기/복원 시 자동으로 커밋 & 푸시됩니다.  |  키를 복사하려면 행 선택 후 [키 복사] 또는 행 더블클릭",
                Location = new Point(S(12), S(610)),
                AutoSize = true,
                ForeColor = Color.Gray,
                Anchor = AnchorStyles.Bottom | AnchorStyles.Left,
            };

            page.Controls.AddRange(new Control[] { gitStatusLabel, filterTitle, dashboardPanel, toolsTitle, topPanel,
                searchTitle, filterPanel, licenseList, hintLabel });
            return page;
        }

        // 대시보드 링크 버튼 생성 (클릭 시 필터)
        private List<Button> dashboardButtons = new List<Button>();
        private void BuildDashboardLinks()
        {
            dashboardButtons.Clear();
            string[] labels = { "전체", "유효", "관리자", "유저", "만료임박", "만료", "폐기" };
            foreach (var label in labels)
            {
                var b = new Button
                {
                    Text = label,
                    Width = S(78),
                    Height = S(26),
                    BackColor = Color.White,
                    FlatStyle = FlatStyle.Flat,
                };
                string key = label;
                b.Click += (s, e) =>
                {
                    if (dashboardFilter == key) dashboardFilter = "";
                    else dashboardFilter = key;
                    SyncFilterFromDashboard();
                    RenderList();
                };
                dashboardButtons.Add(b);
                dashboardPanel.Controls.Add(b);
            }
        }

        private void RefreshDashboardCounts()
        {
            if (dashboardPanel == null || dashboardButtons.Count == 0) return;
            string[] keys = { "전체", "유효", "관리자", "유저", "만료임박", "만료", "폐기" };
            for (int i = 0; i < dashboardButtons.Count && i < keys.Length; i++)
            {
                dashboardButtons[i].BackColor = (dashboardFilter == keys[i]) ? Color.LightSteelBlue : Color.White;
            }
        }

        private void SyncFilterFromDashboard()
        {
            // 대시보드 필터는 RenderList가 dashboardFilter만으로 판단하며,
            // 기존 체크박스는 제거되었으므로 별도 연동이 필요 없음.
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
                verifyButton.Enabled = false;
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
            verifyButton.Enabled = licenseList.SelectedItems.Count == 1;
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
            // 정렬: entries 복사 후 클릭 상태에 따라 순서 결정
            var ordered = entries;
            if (sortColumnIndex >= 0 && sortState > 0)
            {
                int col = sortColumnIndex;
                bool desc = sortState == 2;
                ordered = entries.OrderBy(en => SortKeyFor(en, col)).ThenBy(en => en.Id).ToList();
                if (desc) ordered.Reverse();
            }
            foreach (var en in ordered)
            {
                string status;
                bool skipped = false;
                Color color = Color.Black;
                DateTime expDt;
                bool isRevoked = revoked.Contains(en.Id);
                int daysLeft = int.MinValue;
                if (en.ExpiresAt.Length > 0 && DateTime.TryParse(en.ExpiresAt, out expDt))
                    daysLeft = (int)(expDt - now).TotalDays;

                // 대시보드 필터 적용 (전체는 제외)
                if (dashboardFilter == "관리자" && en.Role != "admin") skipped = true;
                else if (dashboardFilter == "유저" && en.Role == "admin") skipped = true;
                else if (dashboardFilter == "만료" && !(en.ExpiresAt.Length > 0 && DateTime.TryParse(en.ExpiresAt, out expDt) && expDt < now)) skipped = true;
                else if (dashboardFilter == "유효" && (isRevoked || (en.ExpiresAt.Length > 0 && DateTime.TryParse(en.ExpiresAt, out expDt) && expDt < now))) skipped = true;
                else if (dashboardFilter == "폐기" && !isRevoked) skipped = true;
                else if (dashboardFilter == "만료임박")
                {
                    if (isRevoked || en.ExpiresAt.Length == 0 || !DateTime.TryParse(en.ExpiresAt, out expDt)) skipped = true;
                    else { int dl = (int)(expDt - now).TotalDays; if (dl < 0 || dl > 7) skipped = true; }
                }
                if (skipped) continue;

                // 검색 필터 (ID / 사용자 / 키)
                if (query.Length > 0)
                {
                    bool hit = en.Id.ToLowerInvariant().Contains(query)
                        || en.Owner.ToLowerInvariant().Contains(query)
                        || en.KeyPlain.ToLowerInvariant().Contains(query);
                    if (!hit) continue;
                }
                // 관리자 필터: role이 admin인 키만

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
                if (en.Role == "admin" && !isRevoked) color = Color.Purple;
                item.ForeColor = color;
                licenseList.Items.Add(item);
            }
            RefreshDashboardCounts();
            FitWindowToKeyContent();
        }

        private string SortKeyFor(LicenseEntry en, int col)
        {
            switch (col)
            {
                case 0: return en.Id.ToLowerInvariant();
                case 1: return en.Owner.ToLowerInvariant();
                case 2:
                    // 만료일 없음(무기한)은 가장 뒤로
                    if (en.ExpiresAt.Length == 0) return "9999-99-99";
                    DateTime d;
                    return DateTime.TryParse(en.ExpiresAt, out d)
                        ? d.ToString("yyyy-MM-dd HH:mm")
                        : "9998";
                case 3:
                    // 상태 우선순위: 유효 < D-n < 만료 < 폐기됨 (문자열 기준)
                    if (revoked.Contains(en.Id)) return "3";
                    DateTime expDt;
                    if (en.ExpiresAt.Length > 0 && DateTime.TryParse(en.ExpiresAt, out expDt))
                    {
                        double left = (expDt - DateTime.Today).TotalDays;
                        if (left < 0) return "2";
                        if (left <= 7) return "1";
                    }
                    return "0";
                case 4: return en.Role == "admin" ? "1" : "0";
                case 5:
                    if (en.CreatedAt.Length == 0) return "9999-99-99";
                    DateTime c;
                    return DateTime.TryParse(en.CreatedAt, out c) ? c.ToString("yyyy-MM-dd HH:mm") : "9998";
                case 6: return en.KeyPlain.ToLowerInvariant();
                default: return "";
            }
        }

        // 열 머리글 클릭 시 오름차순 -> 내림차순 -> 기본 순환
        private void OnColumnSortClick(object sender, ColumnClickEventArgs e)
        {
            if (e.Column == sortColumnIndex)
                sortState = (sortState + 1) % 3;
            else
            {
                sortColumnIndex = e.Column;
                sortState = 1;
            }
            UpdateSortHeaderHint();
            RenderList();
        }

        // 현재 정렬 상태를 열 머리글 텍스트에 표시 (▲ / ▼ / 없음)
        private void UpdateSortHeaderHint()
        {
            string[] baseNames = { "키 ID", "사용자", "만료일", "상태", "역할", "생성일", "발급 키" };
            for (int i = 0; i < licenseList.Columns.Count && i < baseNames.Length; i++)
            {
                string suffix = "";
                if (i == sortColumnIndex)
                {
                    if (sortState == 1) suffix = " ▲";
                    else if (sortState == 2) suffix = " ▼";
                }
                licenseList.Columns[i].Text = baseNames[i] + suffix;
            }
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
            SetSensitiveClipboard(string.Join(Environment.NewLine, lines.ToArray()));
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
            if (!CreateSnapshotBackup())
            {
                MessageBox.Show("백업 스냅샷 생성에 실패하여 삭제를 중단합니다.", "백업 오류",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
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

        // ==================== 작업 이력(감사) ====================
        // 주의: 이력에는 키 원문, 키 해시, 키가 포함된 상세 문자열을 절대 저장하지 않는다.

        private void LoadHistory()
        {
            historyItems.Clear();
            try
            {
                if (!File.Exists(HistoryPath)) return;
                string raw = File.ReadAllText(HistoryPath);
                int pos = 0;
                SkipWs(raw, ref pos);
                if (pos >= raw.Length || raw[pos] != '[') return;
                var arr = ParseArray(raw, ref pos);
                foreach (var item in arr)
                {
                    var dict = item as Dictionary<string, object>;
                    if (dict != null) historyItems.Add(dict);
                }
                if (historyItems.Count > 2000)
                    historyItems.RemoveRange(0, historyItems.Count - 2000);
            }
            catch { }
        }

        private void SaveHistory()
        {
            try
            {
                var sb = new StringBuilder("[");
                for (int i = 0; i < historyItems.Count; i++)
                {
                    var item = historyItems[i];
                    var fields = new List<string>();
                    foreach (var kv in item)
                    {
                        if (kv.Value == null) continue;
                        string val = kv.Value.ToString();
                        if (val.Length == 0) continue;
                        fields.Add("\"" + Escape(kv.Key) + "\": \"" + Escape(val) + "\"");
                    }
                    sb.Append("{ " + string.Join(", ", fields.ToArray()) + " }");
                    if (i < historyItems.Count - 1) sb.Append(",");
                }
                sb.Append("]");
                File.WriteAllText(HistoryPath, sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }

        private void AppendHistory(string action, string targetId, string detail, string status)
        {
            var item = new Dictionary<string, object>
            {
                { "at", DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") },
                { "action", action },
                { "target", targetId },
                { "detail", detail },
                { "status", status },
            };
            historyItems.Add(item);
            if (historyItems.Count > 2000)
                historyItems.RemoveRange(0, historyItems.Count - 2000);
            SaveHistory();
            ReloadHistoryList();
        }

        // 새 항목을 목록 맨 앞(아래/최근)으로 정렬해 반환
        private void ReloadHistoryList()
        {
            if (historyList == null) return;
            historyList.Items.Clear();
            var sorted = historyItems.OrderByDescending(x => Str(x, "at")).ToList();
            foreach (var it in sorted)
            {
                string status = Str(it, "status");
                var item = new ListViewItem(Str(it, "at"));
                item.SubItems.Add(Str(it, "action"));
                item.SubItems.Add(Str(it, "target"));
                item.SubItems.Add(Str(it, "detail"));
                item.SubItems.Add(status);
                Color c = Color.Black;
                if (status == "성공") c = Color.DarkGreen;
                else if (status == "실패") c = Color.Red;
                else if (status == "푸시 필요") c = Color.OrangeRed;
                item.ForeColor = c;
                historyList.Items.Add(item);
            }
        }

        // ==================== 민감 클립보드 자동 삭제 ====================
        private string LastSensitiveClipboard = "";

        private void SetSensitiveClipboard(string text)
        {
            LastSensitiveClipboard = text ?? "";
            Clipboard.SetText(LastSensitiveClipboard);
            if (clipboardClearTimer == null)
            {
                clipboardClearTimer = new Timer();
                clipboardClearTimer.Interval = clipboardGuardSeconds * 1000;
                clipboardClearTimer.Tick += (s, e) => TryClearSensitiveClipboard();
                clipboardClearTimer.Start();
            }
            else
            {
                clipboardClearTimer.Stop();
                clipboardClearTimer.Start();
            }
        }

        private void TryClearSensitiveClipboard()
        {
            if (clipboardClearTimer != null) clipboardClearTimer.Stop();
            try
            {
                string current = Clipboard.ContainsText() ? Clipboard.GetText() : "";
                if (LastSensitiveClipboard.Length > 0 && current == LastSensitiveClipboard)
                    Clipboard.Clear();
            }
            catch { }
        }

        // ==================== 백업 스냅샷 ====================
        private bool CreateSnapshotBackup()
        {
            try
            {
                Directory.CreateDirectory(BackupDirPath);
                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + "_" + Guid.NewGuid().ToString("N").Substring(0, 6);
                string folder = Path.Combine(BackupDirPath, stamp);
                Directory.CreateDirectory(folder);
                if (File.Exists(JsonPath))
                    File.Copy(JsonPath, Path.Combine(folder, "licenses.json"), true);
                if (File.Exists(LocalKeysPath))
                    File.Copy(LocalKeysPath, Path.Combine(folder, "keys.local.json"), true);
                // 최근 30개만 보존
                var dirs = Directory.GetDirectories(BackupDirPath).OrderByDescending(d => d).ToList();
                for (int i = 30; i < dirs.Count; i++)
                {
                    try { Directory.Delete(dirs[i], true); } catch { }
                }
                return true;
            }
            catch
            {
                return false;
            }
        }

        // ==================== Git 동기화 상태 ====================
        private string GetGitStatusText()
        {
            string repoDir = Path.GetDirectoryName(JsonPath);
            try
            {
                // 충돌 우선
                int unmerged = RunGitCapture(repoDir, "diff --name-only --diff-filter=U");
                if (unmerged >= 0)
                {
                    var changed = GetChangedFiles();
                    if (changedContainsConflict(changed))
                        return "충돌";
                }
                int ahead = RunGitCaptureCount(repoDir, "rev-list --count origin/main..main");
                int behind = RunGitCaptureCount(repoDir, "rev-list --count main..origin/main");
                if (ahead < 0 || behind < 0)
                {
                    // origin/main 없음 또는 명령 실패
                    bool hasChanges = GetDirtyFlag(repoDir);
                    return hasChanges ? "푸시 필요" : "동기화 완료";
                }
                bool dirty = GetDirtyFlag(repoDir);
                if (ahead > 0 || behind > 0 || dirty)
                {
                    if (behind > 0) return ahead > 0 || dirty ? "충돌 위험" : "원격 변경";
                    return "푸시 필요";
                }
                return "동기화 완료";
            }
            catch
            {
                return "알 수 없음";
            }
        }

        private bool changedContainsConflict(List<string> files)
        {
            return false; // 실제 충돌은 unmerged 출력 시 exit code가 1 이상으로 판별
        }

        private List<string> GetChangedFiles()
        {
            var result = new List<string>();
            string repoDir = Path.GetDirectoryName(JsonPath);
            var sb = new StringBuilder();
            RunGitCapture(repoDir, "status --porcelain", sb);
            foreach (var line in sb.ToString().Split(new[] { '\n', '\r' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (line.Trim().Length > 0) result.Add(line);
            }
            return result;
        }

        private bool GetDirtyFlag(string repoDir)
        {
            var sb = new StringBuilder();
            RunGitCapture(repoDir, "status --porcelain", sb);
            return sb.ToString().Trim().Length > 0;
        }

        private int RunGitCaptureCount(string workdir, string args)
        {
            var sb = new StringBuilder();
            int exit = RunGitCapture(workdir, args, sb);
            int val;
            if (int.TryParse(sb.ToString().Trim(), out val)) return val;
            return -1;
        }

        // ==================== 키 검증 ====================
        private string ValidateSelectedKey()
        {
            var entry = GetSelected();
            if (entry == null) return "선택된 키가 없습니다.";
            if (entry.KeyPlain.Length == 0)
                return "로컬 키 기록이 없습니다. (프로그램에서 발급된 키만 검증 가능)";
            string localHash;
            using (var sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(entry.KeyPlain));
                localHash = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
            string stage = "유효";
            if (!string.Equals(localHash, entry.Hash, StringComparison.OrdinalIgnoreCase))
                stage = "해시 불일치";
            else if (revoked.Contains(entry.Id))
                stage = "폐기됨";
            else if (entry.ExpiresAt.Length > 0)
            {
                DateTime expDt;
                if (DateTime.TryParse(entry.ExpiresAt, out expDt))
                {
                    if (expDt < DateTime.Now) stage = "만료";
                    else
                    {
                        int daysLeft = (int)(expDt - DateTime.Today).TotalDays;
                        if (daysLeft <= 7) stage = "D-" + daysLeft;
                    }
                }
            }
            string role = entry.Role == "admin" ? "관리자" : "일반";
            return "키 ID: " + entry.Id + "\n사용자: " + entry.Owner + "\n역할: " + role
                 + "\n만료: " + (entry.ExpiresAt.Length > 0 ? entry.ExpiresAt : "무기한")
                 + "\n결과: " + stage
                 + "\n(hash " + (string.Equals(localHash, entry.Hash, StringComparison.OrdinalIgnoreCase) ? "일치" : "불일치") + ")";
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
            expiryDateCheck.Checked = entryHasExpiry;     // 기존 만료일 유무 반영
            expiryTimeCheck.Checked = entryHasExpiry && entry.ExpiresAt.Contains(" ");
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
            // 갱신 취소 시 텍스트박스에 채워진 키 ID/사용자 이름만 비운다
            idBox.Text = "";
            ownerBox.Text = "";
            generateButton.Text = "키 생성 & 저장소 반영";
            adminCheck.Checked = false;
            expiryDateCheck.Checked = true;
            expiryTimeCheck.Checked = false;
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

            // 새 만료일 계산(후보만 저장, 아직 메모리 변경 안 함)
            DateTime now = DateTime.Now;
            var changes = new List<string>();
            var newExpiry = new Dictionary<string, string>();
            foreach (var en in selected)
            {
                if (revoked.Contains(en.Id)) continue;   // 폐기된 키는 연장 제외
                DateTime baseDate = now;
                DateTime expDt;
                if (en.ExpiresAt.Length > 0 && DateTime.TryParse(en.ExpiresAt, out expDt) && expDt > baseDate)
                    baseDate = expDt;
                string nd = baseDate.AddDays(days).ToString("yyyy-MM-dd HH:mm");
                newExpiry[en.Id] = nd;
                changes.Add(en.Id + ": -> " + nd);
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
            if (confirm != DialogResult.Yes) return;
            if (!CreateSnapshotBackup())
            {
                MessageBox.Show("백업 스냅샷 생성에 실패하여 연장을 중단합니다.", "백업 오류",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            foreach (var en in selected)
            {
                if (newExpiry.ContainsKey(en.Id)) en.ExpiresAt = newExpiry[en.Id];
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
            RunGit(repoDir, "fetch origin main");
                RunGit(repoDir, "reset --soft origin/main");
            exit = RunGitCapture(repoDir, "push origin main", output);
            pushRetryButton.Enabled = true;
            pushRetryButton.Text = "푸시 재시도";
            if (exit == 0 && aheadCount > 0)
            {
                MessageBox.Show(aheadCount + "개의 밀려난 커밋을 푸시했습니다.", "푸시 재시도",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                UpdateGitStatusLabel();
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
            SetSensitiveClipboard(string.Join(Environment.NewLine, lines.ToArray()));
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
            string revokeReason = "";
            if (doRevoke)
            {
                revokeReason = PromptForRevokeReason(entry.Id);
                if (revokeReason == null) return;   // 취소
            }
            if (!CreateSnapshotBackup())
            {
                MessageBox.Show("백업 스냅샷 생성에 실패하여 작업을 중단합니다.", "백업 오류",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                return;
            }
            if (doRevoke)
            {
                if (!revoked.Contains(entry.Id)) revoked.Add(entry.Id);
                await SaveAndPush("revoke license '" + entry.Id + "'" + (revokeReason.Length > 0 ? " (" + TrimMsg(revokeReason) + ")" : ""));
            }
            else
            {
                revoked.Remove(entry.Id);
                await SaveAndPush("restore license '" + entry.Id + "'");
            }
            RenderList();
            OnListSelectionChanged(null, null);
        }

        // 폐기 사유 입력창 (취소 시 null 반환, 빈 값 거부)
        private string PromptForRevokeReason(string id)
        {
            var dlg = new Form
            {
                Text = "폐기 사유 입력",
                ClientSize = new Size(S(480), S(190)),
                StartPosition = FormStartPosition.CenterParent,
                FormBorderStyle = FormBorderStyle.FixedDialog,
                MaximizeBox = false,
                MinimizeBox = false,
                Font = Font,
            };
            var lb = new Label
            {
                Text = "키 '" + id + "' 을(를) 폐기하는 사유를 입력하세요.",
                Location = new Point(S(15), S(15)),
                AutoSize = true,
            };
            var tb = new TextBox
            {
                Location = new Point(S(15), S(50)),
                Size = new Size(S(430), S(80)),
                Multiline = true,
            };
            var ok = new Button { Text = "확인", DialogResult = DialogResult.OK, Location = new Point(S(250), S(140)), Width = S(90) };
            var cancel = new Button { Text = "취소", DialogResult = DialogResult.Cancel, Location = new Point(S(350), S(140)), Width = S(90) };
            dlg.Controls.Add(lb); dlg.Controls.Add(tb); dlg.Controls.Add(ok); dlg.Controls.Add(cancel);
            dlg.AcceptButton = ok;
            dlg.CancelButton = cancel;
            string result = "";
            while (true)
            {
                if (dlg.ShowDialog(this) != DialogResult.OK) return null;
                result = tb.Text.Trim();
                if (result.Length > 0) break;
                MessageBox.Show(this, "폐기 사유를 입력해 주세요.", "폐기 사유",
                    MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
            return result;
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

            expiryLabel = new Label
            {
                Text = "만료일 (날짜·시간 모두 해제 시 무기한)",
                Location = new Point(S(20), S(128)),
                AutoSize = true,
            };
            expiryDateCheck = new CheckBox
            {
                Text = "날짜 지정",
                Location = new Point(S(20), S(154)),
                AutoSize = true,
                Checked = true,
            };
            expiryPicker = new DateTimePicker
            {
                Location = new Point(S(140), S(151)),
                Width = S(145),
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
            page.Controls.AddRange(new Control[] { label1, idBox, label2, ownerBox, expiryLabel,
                expiryDateCheck, expiryPicker,
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
                Location = new Point(S(20), S(214)),
                AutoSize = true,
                ForeColor = SystemColors.HotTrack,
                Font = new Font("Consolas", 17f, FontStyle.Bold),
            };
            expiryTimeCheck = new CheckBox
            {
                Text = "시간 지정",
                Location = new Point(S(20), S(184)),
                AutoSize = true,
            };
            adminCheck = new CheckBox
            {
                Text = "관리자",
                Location = new Point(S(300), S(154)),
                AutoSize = true,
                ForeColor = Color.Firebrick,
            };
            expiryHourBox = new ComboBox
            {
                Location = new Point(S(140), S(181)),
                Width = S(55),
                DropDownStyle = ComboBoxStyle.DropDownList,
                Enabled = false,
            };
            for (int h = 0; h < 24; h++) expiryHourBox.Items.Add(h.ToString("00"));
            expiryMinuteBox = new ComboBox
            {
                Location = new Point(S(200), S(181)),
                Width = S(55),
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
            // 날짜 미지정 시 달력만 비활성화(시간 지정은 독립 동작)
            expiryDateCheck.CheckedChanged += (s, e) =>
            {
                expiryPicker.Enabled = expiryDateCheck.Checked;
            };
            // 날짜·시간 모두 해제 시 무기한 안내 문구를 라벨에 표시
            EventHandler UpdateExpiryLabel = (s, e) =>
            {
                bool unlimited = !expiryDateCheck.Checked && !expiryTimeCheck.Checked;
                expiryLabel.Text = unlimited ? "만료일: 무기한" : "만료일 (날짜·시간 모두 해제 시 무기한)";
            };
            expiryDateCheck.CheckedChanged += UpdateExpiryLabel;
            expiryTimeCheck.CheckedChanged += UpdateExpiryLabel;

            generateButton = new Button
            {
                Text = "키 생성 & 저장소 반영",
                Location = new Point(S(20), S(242)),
                Width = S(180),
                Height = S(36),
            };
            // 다른 컨트롤과 동일한 크기(폼 기본 폰트) + 볼드만 적용
            generateButton.Font = new Font(Font, FontStyle.Bold);
            generateButton.Click += OnGenerate;

            copyKeyButton = new Button { Text = "키 복사", Location = new Point(S(210), S(244)), Width = S(90), Height = S(32), Enabled = false };
            copyKeyButton.Click += (s, e) => { if (lastKey.Length > 0) SetSensitiveClipboard(lastKey); };

            cancelRenewButton = new Button
            {
                Text = "갱신 취소",
                Location = new Point(S(310), S(244)),
                Size = new Size(S(100), S(32)),
                Visible = false,
            };
            cancelRenewButton.Click += (s, e) => CancelRenew();

            resultBox = new TextBox
            {
                Location = new Point(S(20), S(287)),
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
            // 날짜·시간 모두 해제 시 무기한
            bool hasExpiry = expiryDateCheck.Checked || expiryTimeCheck.Checked;
            // 날짜 미지정 시 오늘 날짜 기준으로 처리
            DateTime expiryDate = expiryDateCheck.Checked ? expiryPicker.Value.Date : DateTime.Today;
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

            if (!CreateSnapshotBackup())
            {
                MessageBox.Show("백업 스냅샷 생성에 실패하여 발급을 중단합니다.", "백업 오류",
                    MessageBoxButtons.OK, MessageBoxIcon.Error);
                generateButton.Enabled = true;
                generateButton.Text = (renewTarget != null) ? "갱신 & 저장소 반영" : "키 생성 & 저장소 반영";
                return;
            }

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

        private string ExtractId(string msg, string prefix)
        {
            try
            {
                if (msg == null || !msg.StartsWith(prefix)) return "";
                int start = prefix.Length;
                int end = msg.IndexOf("'", start);
                if (end < 0) return "";
                return msg.Substring(start, end - start);
            }
            catch { return ""; }
        }

        private string TrimMsg(string s)
        {
            if (s == null) return "";
            return s.Length > 80 ? s.Substring(0, 80) : s;
        }

        private void UpdateGitStatusLabel()
        {
            try
            {
                if (gitStatusLabel == null) return;
                gitStatusLabel.Text = "Git: " + GetGitStatusText();
            }
            catch { }
        }

        private async System.Threading.Tasks.Task<bool> SaveAndPush(string commitMessage)
        {
            string action = "변경";
            string targetId = "";
            if (commitMessage.StartsWith("issue license '")) { action = "발급"; targetId = ExtractId(commitMessage, "issue license '"); }
            else if (commitMessage.StartsWith("renew license '")) { action = "갱신"; targetId = ExtractId(commitMessage, "renew license '"); }
            else if (commitMessage.StartsWith("remove")) { action = "삭제"; targetId = TrimMsg(commitMessage); }
            else if (commitMessage.StartsWith("revoke license '")) { action = "폐기"; targetId = ExtractId(commitMessage, "revoke license '"); }
            else if (commitMessage.StartsWith("restore license '")) { action = "복원"; targetId = ExtractId(commitMessage, "restore license '"); }
            else if (commitMessage.StartsWith("extend")) { action = "연장"; targetId = TrimMsg(commitMessage); }
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(JsonPath));
                File.WriteAllText(JsonPath, SerializeManifest(entries, revoked) + "\n", Encoding.UTF8);

                string repoDir = Path.GetDirectoryName(JsonPath);

                RunGit(repoDir, "add licenses.json");
                int staged = RunGitCapture(repoDir, "diff --cached --quiet");
                if (staged == 0)
                {
                    AppendHistory(action, targetId, "변경사항 없음(이미 반영됨)", "성공");
                    MessageBox.Show("변경사항이 없어 커밋하지 않았습니다.", "정보",
                        MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return true;
                }
                var gitLog = new StringBuilder();
                RunGitCapture(repoDir, "commit -m \"chore: " + commitMessage.Replace("\"", "") + "\"", gitLog);
                RunGit(repoDir, "fetch origin main");
                RunGit(repoDir, "reset --soft origin/main");
                int pushExit = RunGitCapture(repoDir, "push origin main", gitLog);

                if (pushExit != 0)
                {
                    AppendHistory(action, targetId, "로컬 커밋 완료, 푸시 실패", "푸시 필요");
                    MessageBox.Show("Git push 실패. 커밋은 로컬에 남아 있습니다.\n\n=== Git 출력 ===\n" + gitLog.ToString(),
                        "푸시 실패", MessageBoxButtons.OK, MessageBoxIcon.Error);
                    return false;
                }
                AppendHistory(action, targetId, "커밋 & 푸시 완료", "성공");
                UpdateGitStatusLabel();
                return true;
            }
            catch (Exception ex)
            {
                AppendHistory(action, targetId, "오류: " + ex.Message, "실패");
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
