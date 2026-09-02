using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace AxialSqlTools
{
    /// <summary>
    /// Centralizes UI localization. English XAML text is used as the stable key so
    /// existing views can be localized without duplicating every XAML file.
    /// </summary>
    internal static class LocalizationManager
    {
        public const string ChineseLanguage = "zh-CN";
        public const string EnglishLanguage = "en-US";

        private sealed class OriginalValues
        {
            public readonly Dictionary<string, string> Values = new Dictionary<string, string>();
            public readonly Dictionary<string, string> LastApplied = new Dictionary<string, string>();
        }

        private sealed class ReferenceComparer : IEqualityComparer<DependencyObject>
        {
            public bool Equals(DependencyObject x, DependencyObject y) => ReferenceEquals(x, y);
            public int GetHashCode(DependencyObject obj) => RuntimeHelpers.GetHashCode(obj);
        }

        private static readonly ConditionalWeakTable<DependencyObject, OriginalValues> Originals =
            new ConditionalWeakTable<DependencyObject, OriginalValues>();
        private static bool automaticLocalizationEnabled;

        private static readonly Dictionary<string, string> Chinese = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["Axial SQL Tools - Settings"] = "Axial SQL Tools - 设置",
            ["Settings"] = "设置", ["About"] = "关于", ["Tools"] = "工具",
            ["Language"] = "语言", ["Interface language:"] = "界面语言：",
            ["Simplified Chinese"] = "简体中文", ["English"] = "English",
            ["The language setting applies immediately to Axial SQL Tools windows. Toolbar command labels use the package default language."] = "语言设置会立即应用到 Axial SQL Tools 窗口。工具栏命令标签使用扩展包的默认语言。",
            ["Apply"] = "应用", ["Save"] = "保存", ["Cancel"] = "取消", ["Close"] = "关闭",
            ["OK"] = "确定", ["Yes"] = "是", ["No"] = "否", ["Edit"] = "编辑",
            ["Delete"] = "删除", ["Remove"] = "移除", ["Refresh"] = "刷新",
            ["Search"] = "搜索", ["Loading..."] = "正在加载…", ["Error"] = "错误",
            ["Warning"] = "警告", ["Success"] = "成功", ["Status"] = "状态",
            ["Feature description in"] = "功能说明：", ["Wiki"] = "Wiki",
            ["Query Templates"] = "查询模板", ["Templates Folder:"] = "模板文件夹：",
            ["Select folder..."] = "选择文件夹…", ["Useful TSQL scripts"] = "实用 T-SQL 脚本",
            ["Download TSQL scripts from GitHub"] = "从 GitHub 下载 T-SQL 脚本",
            ["Code Snippets"] = "代码片段", ["Use code snippets (SSMS restart required)"] = "启用代码片段（需要重启 SSMS）",
            ["Snippets Location:"] = "代码片段位置：", ["Replace snippets when pressing:"] = "按下以下按键时替换代码片段：",
            ["Replace SELECT * with column list"] = "将 SELECT * 替换为列列表",
            ["Replace asterisk when pressing:"] = "按下以下按键时替换星号：",
            ["Query History"] = "查询历史", ["Storage Type:"] = "存储类型：",
            ["Disabled"] = "禁用", ["Database table"] = "数据库表", ["Text files (JSONL)"] = "文本文件（JSONL）",
            ["Text Files:"] = "文本文件：", ["Open folder"] = "打开文件夹", ["Connection Info:"] = "连接信息：",
            ["< not configured >"] = "< 未配置 >", ["Use connection from Object Explorer"] = "使用对象资源管理器中的连接",
            ["Target Table Name:"] = "目标表名：", ["Creation script (for information only)"] = "创建脚本（仅供参考）",
            ["Code Format"] = "代码格式", ["Preserve comments"] = "保留注释",
            ["Remove new line after JOIN"] = "移除 JOIN 后的换行", ["Add tab after JOIN..ON"] = "在 JOIN…ON 后添加缩进",
            ["Place CROSS/OUTER JOIN/APPLY on a new line"] = "将 CROSS/OUTER JOIN/APPLY 放在新行",
            ["Format CASE expression as multiline"] = "将 CASE 表达式格式化为多行",
            ["Add new line between statements in code blocks"] = "在代码块的语句之间添加空行",
            ["Break exec sproc parameters per line"] = "存储过程执行参数逐行显示",
            ["Always upper-case built-in functions"] = "内置函数始终大写",
            ["Unindent Begin..End blocks"] = "取消 BEGIN…END 块缩进",
            ["Break variable definitions per line"] = "变量定义逐行显示",
            ["Break sproc definition parameters per line"] = "存储过程定义参数逐行显示",
            ["Source query"] = "源查询", ["Formatted query"] = "格式化后的查询",
            ["Excel Export"] = "Excel 导出", ["Google Sheets"] = "Google 表格",
            ["Default Directory:"] = "默认目录：", ["Default Filename:"] = "默认文件名：",
            ["Default Spreadsheet Title:"] = "默认电子表格标题：", ["Client ID:"] = "客户端 ID：",
            ["Client Secret:"] = "客户端密钥：", ["Authorization Status:"] = "授权状态：",
            ["Authorize Google Sheets"] = "授权 Google 表格", ["Authorized"] = "已授权",
            ["Connection Colors"] = "连接颜色", ["Add new rule"] = "添加新规则",
            ["Keyboard shortcuts"] = "键盘快捷键", ["Script Object Definition"] = "对象定义脚本",
            ["Open definition script:"] = "打开定义脚本：",
            ["Select an object name in the SQL editor, then use this shortcut to open its definition script. Enter None to remove the shortcut."] = "在 SQL 编辑器中选择对象名称，然后使用此快捷键打开其定义脚本。输入 None 可解除快捷键。",
            ["Examples: F12, Ctrl+F12, Ctrl+Shift+O, or None"] = "示例：F12、Ctrl+F12、Ctrl+Shift+O 或 None",
            ["Enter a shortcut such as F12, Ctrl+F12, or Ctrl+Shift+O. Enter None to remove it."] = "请输入 F12、Ctrl+F12 或 Ctrl+Shift+O 等快捷键。输入 None 可解除快捷键。",
            ["The shortcut could not be applied: "] = "无法应用快捷键：",
            ["The shortcut was applied, but the setting could not be saved."] = "快捷键已应用，但无法保存该设置。",
            ["Configured rules"] = "已配置的规则", ["Server name contains:"] = "服务器名称包含：",
            ["Database name contains:"] = "数据库名称包含：", ["Status bar and tab color:"] = "状态栏和标签页颜色：",
            ["Pick color..."] = "选择颜色…", ["+ Add"] = "+ 添加", ["Edit selected"] = "编辑所选项",
            ["Remove selected"] = "移除所选项", ["Server contains"] = "服务器包含", ["Database contains"] = "数据库包含",
            ["Enabled"] = "启用", ["Move up"] = "上移", ["Move down"] = "下移", ["Edit rule"] = "编辑规则",
            ["Save changes"] = "保存修改", ["Cancel edit"] = "取消编辑", ["Unsaved changes"] = "有未保存的更改",
            ["No unsaved changes"] = "没有未保存的更改",
            ["The connection color rules could not be saved. Please try again."] = "连接颜色规则无法保存，请重试。",
            ["SQL completion"] = "SQL 自动补全",
            ["Up/Down select | Enter/Tab insert | Esc close"] = "上下键选择 | Enter/Tab 插入 | Esc 关闭",
            ["Showing first 200 matches - keep typing to narrow results"] = "显示前 200 个匹配项，请继续输入以缩小范围",
            ["{0} matches | Up/Down select | Enter/Tab insert | Esc close"] = "{0} 个匹配项 | 上下键选择 | Enter/Tab 插入 | Esc 关闭",
            ["Color"] = "颜色", ["SMTP Settings"] = "SMTP 设置", ["Sender email address:"] = "发件人邮箱：",
            ["SMTP user name:"] = "SMTP 用户名：", ["SMTP password:"] = "SMTP 密码：",
            ["SMTP server:"] = "SMTP 服务器：", ["SMTP port:"] = "SMTP 端口：", ["Enable SSL/TLS"] = "启用 SSL/TLS",
            ["Updates"] = "更新", ["Check for Axial SQL Tools updates on startup"] = "启动时检查 Axial SQL Tools 更新",
            ["Check for updates"] = "检查更新", ["Update status"] = "更新状态", ["GitHub Integration"] = "GitHub 集成",
            ["GitHub Token:"] = "GitHub 令牌：", ["API key:"] = "API 密钥：",
            ["Quick Search"] = "快速搜索", ["Snippet Manager"] = "代码片段管理器",
            ["Statistics Summary"] = "统计信息摘要", ["SQL Server Builds"] = "SQL Server 版本",
            ["Data Transfer"] = "数据传输", ["Sync to GitHub"] = "同步到 GitHub",
            ["Data Import"] = "数据导入", ["Grid to Email"] = "结果网格转邮件",
            ["Health Dashboard | Server"] = "健康面板 | 服务器", ["Axial SQL Tools | Settings"] = "Axial SQL Tools | 设置",
            ["Profiles"] = "配置文件", ["+ Add New Profile"] = "+ 新建配置文件", ["Target Repo Info"] = "目标仓库信息",
            ["Commit"] = "提交", ["Commit Msg:"] = "提交信息：", ["Confirm before pushing"] = "推送前确认",
            ["Script And Commit"] = "生成脚本并提交", ["Edit Sync Profile"] = "编辑同步配置",
            ["Server Health Dashboard"] = "服务器健康面板",
            ["Export"] = "导出", ["Copy"] = "复制", ["Select All"] = "全选", ["Clear"] = "清除",
            ["Database:"] = "数据库：", ["Server:"] = "服务器：", ["Username:"] = "用户名：", ["Password:"] = "密码：",
            ["Connection string:"] = "连接字符串：", ["Test connection"] = "测试连接", ["Connect"] = "连接",
            ["Query"] = "查询", ["Results"] = "结果", ["Duration"] = "耗时", ["Rows"] = "行数",
            ["Name"] = "名称", ["Description"] = "说明", ["Created"] = "创建时间", ["Modified"] = "修改时间",
            ["Database"] = "数据库", ["Server"] = "服务器", ["Username"] = "用户名", ["Password"] = "密码",
            ["Port"] = "端口", ["Table"] = "表", ["Script"] = "脚本", ["Import"] = "导入", ["Open"] = "打开",
            ["Select"] = "选择", ["Reset"] = "重置", ["Copy as"] = "复制为", ["Description:"] = "说明：",
            ["Data Source:"] = "数据源：", ["Server name:"] = "服务器名称：", ["Service name:"] = "服务名称：",
            ["Target database"] = "目标数据库", ["Rename to:"] = "重命名为：", ["Cursor Marker:"] = "光标标记：",
            ["Diagnostic log folder: "] = "诊断日志文件夹：", ["Saved..."] = "已保存…",
            ["A new version is now available!"] = "有新版本可用！", ["All user databases on the server"] = "服务器上的所有用户数据库",
            ["Export Logins and Permissions"] = "导出登录名和权限",
            ["Export SQL Server Agent Parameters - Jobs, Operators, Alerts"] = "导出 SQL Server 代理参数——作业、操作员和警报",
            ["Export SQL Server Configuration Values"] = "导出 SQL Server 配置值",
            ["Enter database name (or part of it)"] = "输入数据库名称（或其中一部分）",
            ["Enter server name (or part of it)"] = "输入服务器名称（或其中一部分）",
            ["Enter text to filter QueryText"] = "输入文本以筛选查询内容",
            ["Use Ola Hallengren keywords such as ALL_DATABASES, USER_DATABASES, etc."] = "可使用 Ola Hallengren 关键字，例如 ALL_DATABASES、USER_DATABASES 等。",
            ["Saved to: %LocalAppData%\\AxialSQL\\data-transfer-connections.json"] = "保存位置：%LocalAppData%\\AxialSQL\\data-transfer-connections.json",
            ["OpenAI - ChatGPT integration"] = "OpenAI - ChatGPT 集成",
            ["Done"] = "完成", ["Something went wrong"] = "发生错误", ["An error occurred"] = "发生错误",
            ["Invalid Email"] = "邮箱地址无效", ["Subject Required"] = "需要填写主题", ["Open in Excel"] = "在 Excel 中打开",
            ["Script Object"] = "生成对象脚本", ["WIP"] = "开发中", ["DataTransferWindow"] = "数据传输",
            ["No Profile Selected"] = "未选择配置文件", ["No Repo Selected"] = "未选择仓库", ["Confirm Delete"] = "确认删除",
            ["Confirm Commit"] = "确认提交", ["Connection Colors"] = "连接颜色",
            ["TSQL script copied to clipboard!"] = "T-SQL 脚本已复制到剪贴板！",
            ["Prefix is required."] = "必须填写前缀。", ["Import completed."] = "导入完成。", ["Settings saved."] = "设置已保存。",
            ["The exported file could not be found."] = "找不到已导出的文件。",
            ["Email has been sent!"] = "邮件已发送！", ["Email has been queued via Database Mail!"] = "邮件已通过数据库邮件进入发送队列！",
            ["Can't parse the recipient's email address."] = "无法解析收件人邮箱地址。", ["Please provide the email subject."] = "请填写邮件主题。",
            ["Invalid mail config"] = "邮件配置无效", ["Select a saved connection."] = "请选择一个已保存的连接。",
            ["Select a connection to save."] = "请选择要保存的连接。",
            ["Please select a server or database node in Object Explorer first."] = "请先在对象资源管理器中选择服务器或数据库节点。",
            ["Select a connection from Object Explorer first."] = "请先从对象资源管理器中选择连接。",
            ["Enter text to search."] = "请输入要搜索的文本。", ["Select at least one object type."] = "请至少选择一种对象类型。",
            ["Search canceled"] = "搜索已取消", ["Search failed"] = "搜索失败", ["Searching..."] = "正在搜索…",
            ["Select the object to script."] = "请选择要生成脚本的对象。", ["Select an object to script."] = "请选择要生成脚本的对象。",
            ["Please select a database in Object Explorer first."] = "请先在对象资源管理器中选择数据库。",
            ["Select an Excel workbook first."] = "请先选择 Excel 工作簿。", ["Select a target database from Object Explorer."] = "请从对象资源管理器中选择目标数据库。",
            ["Provide a destination table name."] = "请输入目标表名。", ["Data transfer has been cancelled."] = "数据传输已取消。",
            ["No saved connections found. Use \"Edit Saved Connections\" to add one."] = "未找到已保存的连接。请使用“编辑已保存连接”添加连接。",
            ["Axial SQL Tool Query Library has been downloaded"] = "Axial SQL Tools 查询库已下载",
            ["Client ID and Client Secret are required before authorizing Google Sheets."] = "授权 Google 表格前必须填写客户端 ID 和客户端密钥。",
            ["Not authorized"] = "未授权", ["Fill in at least the server name or the database name."] = "请至少填写服务器名称或数据库名称。",
            ["Please select a profile to edit."] = "请选择要编辑的配置文件。", ["No profile selected."] = "未选择配置文件。",
            ["Please select a GitHub repo first."] = "请先选择 GitHub 仓库。",
            ["Profile Name, Owner, Repo Name, Branch, and Token are required."] = "必须填写配置名称、所有者、仓库名称、分支和令牌。",
            ["Retrieving data from the source..."] = "正在从数据源读取数据…",
            ["Copied"] = "已复制", ["Copied Column Names"] = "已复制列名", ["No Column Names to Copy"] = "没有可复制的列名",
            ["No cells selected to copy"] = "未选择要复制的单元格", ["No data to copy"] = "没有可复制的数据",
            ["No column selected to copy"] = "未选择要复制的列", ["Copy All As ..."] = "全部复制为…",
            ["Copy Selected As ..."] = "将所选内容复制为…", ["Copy Selected Column Names"] = "复制所选列名",
            ["Copy All Column Names"] = "复制全部列名", ["Values as IN (...) - hold Shift for compact list"] = "将值复制为 IN (...)（按住 Shift 生成紧凑列表）",

            // Settings: snippets, Excel, Google Sheets and connection colors.
            ["Enable Snippets"] = "启用代码片段", ["Save Settings"] = "保存设置",
            ["Save Snippet"] = "保存代码片段", ["Import .sql"] = "导入 .sql", ["Duplicate"] = "复制副本",
            ["Prefix:"] = "前缀：", ["Prefix"] = "前缀", ["Snippet"] = "代码片段", ["Trigger Key:"] = "触发按键：",
            ["Add filter dropdowns to header row (AutoFilter)"] = "在标题行添加筛选下拉框（自动筛选）",
            ["Include content of attached query-window on its own sheet (hold Shift to do the opposite)"] = "将关联查询窗口的内容放入独立工作表（按住 Shift 执行相反操作）",
            ["Export booleans as numbers (TRUE/FALSE -> 1/0)"] = "将布尔值导出为数字（TRUE/FALSE → 1/0）",
            ["Leave blank → Desktop"] = "留空则使用桌面", ["Browse..."] = "浏览…",
            ["Authorize AxialSqlTools to create Google Sheets using an OAuth client ID from "] = "使用来自以下位置的 OAuth 客户端 ID，授权 AxialSqlTools 创建 Google 表格：",
            ["Color query window status bars and document tabs based on the server and/or database name. If both fields are filled, both must match. Leave a field empty to match anything."] = "根据服务器和/或数据库名称设置查询窗口状态栏与文档标签页颜色。若两个字段都填写，则必须同时匹配；留空表示匹配任意值。",
            ["Examples: Server='PROD' matches SQL-PROD-01. Database='master' matches any connection to master. Server='PROD' + Database='Sales' matches only Sales on PROD servers. First matching rule wins."] = "示例：服务器“PROD”可匹配 SQL-PROD-01；数据库“master”可匹配所有 master 连接；服务器“PROD”加数据库“Sales”仅匹配 PROD 服务器上的 Sales。优先使用第一条匹配规则。",
            ["Click to pick a color"] = "单击选择颜色",
            ["e.g. PROD, DEV, localhost, 192.168.1 (leave empty to match any server)"] = "例如 PROD、DEV、localhost、192.168.1（留空则匹配任意服务器）",
            ["e.g. master, MyDB, _prod (leave empty to match any database)"] = "例如 master、MyDB、_prod（留空则匹配任意数据库）",
            ["Leave blank to use the Desktop"] = "留空则使用桌面",
            ["Leave blank to use the default [dbo].[QueryHistory] table."] = "留空则使用默认的 [dbo].[QueryHistory] 表。",
            ["Break SELECT fields after TOP and unindent"] = "在 TOP 后将 SELECT 字段逐行显示并取消缩进",

            // Data transfer and import.
            ["Add New"] = "新建", ["+ New"] = "+ 新建", ["Edit Saved Connections"] = "编辑已保存连接",
            ["Saved Connections"] = "已保存连接", ["Select Saved Connection"] = "选择已保存连接",
            ["No connection selected"] = "未选择连接", ["Set Connection"] = "设置连接",
            ["Source Description"] = "源说明", ["Target Description"] = "目标说明",
            ["Source Query"] = "源查询", ["Target Table"] = "目标表", ["Source"] = "源", ["Destination table"] = "目标表",
            ["Select Source from Object Explorer"] = "从对象资源管理器选择源",
            ["Select Target from Object Explorer"] = "从对象资源管理器选择目标",
            ["Source MySQL Connection"] = "源 MySQL 连接", ["Target MySQL Connection"] = "目标 MySQL 连接",
            ["Source PostgreSQL Connection"] = "源 PostgreSQL 连接", ["Target PostgreSQL Connection"] = "目标 PostgreSQL 连接",
            ["SQL Server -> SQL Server"] = "SQL Server → SQL Server", ["SQL Server -> MySQL"] = "SQL Server → MySQL",
            ["SQL Server -> PostgreSQL"] = "SQL Server → PostgreSQL", ["MySQL -> SQL Server"] = "MySQL → SQL Server",
            ["PostgreSQL -> SQL Server"] = "PostgreSQL → SQL Server",
            ["Copy Data"] = "复制数据", ["(copy progress)"] = "（复制进度）", ["(have not been updated yet)"] = "（尚未更新）",
            ["Clear target table before inserting new records"] = "插入新记录前清空目标表",
            ["Create table if it does not exist"] = "表不存在时创建", ["Automatically create the table if it does not exist"] = "表不存在时自动创建",
            ["Create table structure only (skip data copying)"] = "仅创建表结构（跳过数据复制）",
            ["Additional SqlBulkCopy Options"] = "其他 SqlBulkCopy 选项", ["Treat first row as column headers"] = "将第一行视为列标题",
            ["Truncate the table before importing"] = "导入前截断目标表", ["Excel file"] = "Excel 文件",
            ["Choose an Excel file to get started."] = "请选择一个 Excel 文件开始。", ["Worksheet name"] = "工作表名称",
            ["Optional override when the workbook contains multiple sheets."] = "工作簿包含多个工作表时可在此指定。",
            ["Process checklist"] = "操作步骤", ["Connection Details"] = "连接详情",
            ["1. Choose the Excel workbook that contains the data."] = "1. 选择包含数据的 Excel 工作簿。",
            ["2. Point to the Object Explorer database that will receive the data."] = "2. 在对象资源管理器中选择接收数据的数据库。",
            ["3. Provide the destination table name and confirm optional behaviors."] = "3. 输入目标表名并确认可选设置。",
            ["4. Click Import to perform the one-click upload."] = "4. 单击“导入”执行一键上传。",
            ["Use this window to import Excel spreadsheets into the database currently selected in Object Explorer."] = "使用此窗口将 Excel 电子表格导入对象资源管理器中当前选定的数据库。",

            // Search, history, statistics and common columns.
            ["Search for:"] = "搜索内容：", ["Object types:"] = "对象类型：", ["Match whole words only"] = "仅匹配完整单词",
            ["Use wildcards"] = "使用通配符", ["Check all"] = "全选", ["Uncheck all"] = "取消全选",
            ["Tables"] = "表", ["Views"] = "视图", ["Stored Procedures"] = "存储过程", ["Functions"] = "函数",
            ["Query:"] = "查询：", ["Query (short)"] = "查询（摘要）", ["Elapsed"] = "耗时", ["Elapsed time"] = "耗时",
            ["StartTime"] = "开始时间", ["FinishTime"] = "结束时间", ["Result"] = "结果", ["Login"] = "登录名",
            ["Workstation"] = "工作站", ["Logical reads"] = "逻辑读取", ["Total logical reads"] = "逻辑读取总数",
            ["Scans"] = "扫描次数", ["CPU time"] = "CPU 时间", ["Captured at"] = "捕获时间",
            ["Copy as TSQL"] = "复制为 T-SQL", ["Select formatting options"] = "选择格式化选项",
            ["Query Format Options"] = "查询格式选项", ["Select Object"] = "选择对象", ["Schema"] = "架构",
            ["Object"] = "对象", ["Type"] = "类型", ["Location"] = "位置", ["Provider"] = "提供程序",
            ["Column 1"] = "第 1 列", ["Column 2"] = "第 2 列", ["Match Preview"] = "匹配预览",

            // Health dashboard.
            ["Server Metrics Summary"] = "服务器指标摘要", ["Performance (15m)"] = "性能（15 分钟）",
            ["Database Backups"] = "数据库备份", ["Agent Jobs"] = "代理作业", ["SQL Agent Jobs"] = "SQL Server 代理作业",
            ["Active Connections:"] = "活动连接数：", ["Encrypted Connections:"] = "加密连接数：",
            ["Batch Requests/sec:"] = "每秒批处理请求数：", ["SQL Compilations/sec:"] = "每秒 SQL 编译数：",
            ["CPU %:"] = "CPU %：", ["Memory:"] = "内存：", ["Page Life Expectancy:"] = "页面预期寿命：",
            ["Response Time (ms):"] = "响应时间（毫秒）：", ["Lock Wait Time (sec):"] = "锁等待时间（秒）：",
            ["Total Data File Size (Gb):"] = "数据文件总大小（GB）：", ["Queue Sizes (Gb):"] = "队列大小（GB）：",
            ["Uptime:"] = "运行时间：", ["days"] = "天", ["Refresh Graph"] = "刷新图表", ["See Current Activity:"] = "查看当前活动：",
            ["For the past"] = "过去", ["Include FULL"] = "包含完整备份", ["Include DIFF"] = "包含差异备份",
            ["Include LOG"] = "包含日志备份", ["Unsuccessful executions only"] = "仅失败的执行", ["Be quiet"] = "静默模式",
            ["= server name ="] = "= 服务器名称 =", ["= health ="] = "= 健康状态 =", ["= service name ="] = "= 服务名称 =",
            ["= server uptime ="] = "= 服务器运行时间 =", ["= open connections ="] = "= 打开连接数 =",
            ["= enc connections ="] = "= 加密连接数 =", ["= response time ="] = "= 响应时间 =", ["= wait time ="] = "= 等待时间 =",
            ["= current CPU load ="] = "= 当前 CPU 负载 =", ["= used / total memory ="] = "= 已用/总内存 =",
            ["= PLE ="] = "= 页面预期寿命 =", ["= Batch Requests/sec ="] = "= 每秒批处理请求数 =",
            ["= SQL Compilations/sec ="] = "= 每秒 SQL 编译数 =", ["= blocked request ="] = "= 阻塞请求 =",
            ["= data file size ="] = "= 数据文件大小 =", ["= log file size ="] = "= 日志文件大小 =",
            ["= db status ="] = "= 数据库状态 =", ["= log send queue ="] = "= 日志发送队列 =", ["= redo queue ="] = "= 重做队列 =",

            // GitHub sync, mail and dialogs.
            ["Repository Details:"] = "仓库详情：", ["Owner:"] = "所有者：", ["Repo Name:"] = "仓库名称：",
            ["Branch:"] = "分支：", ["Token:"] = "令牌：", ["Databases:"] = "数据库：", ["Delete Profile"] = "删除配置文件",
            ["Save Profile"] = "保存配置文件", ["Add or update scripted objects"] = "添加或更新已生成脚本的对象",
            ["Sync options will appear here"] = "同步选项将在此显示", ["List of databases will appear here"] = "数据库列表将在此显示",
            ["Email Body:"] = "邮件正文：", ["Recipient Email Address(es). Use semicolons (;) to separate addresses."] = "收件人邮箱地址（多个地址用分号分隔）。",
            ["CC myself"] = "抄送给自己", ["Subject"] = "主题", ["Body:"] = "正文：", ["Body (preview)"] = "正文（预览）",
            ["From:"] = "发件人：", ["To:"] = "收件人：", ["Send"] = "发送", ["File:"] = "文件：",
            ["Export Complete"] = "导出完成", ["The data has been successfully exported."] = "数据已成功导出。",
            ["The data has been exported to Google Sheets."] = "数据已导出到 Google 表格。", ["Saved file:"] = "已保存文件：",

            // About window.
            ["Open-source SSMS productivity tools for SQL Server professionals"] = "面向 SQL Server 专业人员的开源 SSMS 效率工具",
            ["Project links"] = "项目链接", ["GitHub repository"] = "GitHub 仓库", ["Documentation and wiki"] = "文档与 Wiki",
            ["Releases and changelog"] = "版本发布与更新日志", ["Contributing"] = "参与贡献", ["Join project discussions"] = "参与项目讨论",
            ["Report a bug or request a feature"] = "报告问题或提出功能建议", ["License and support"] = "许可证与支持",
            ["Read the full license on GitHub"] = "在 GitHub 阅读完整许可证", ["Version:"] = "版本：",
            ["Axial SQL Tools is a community-oriented extension for SQL Server Management Studio. It brings practical workflow helpers, query utilities, data export options, dashboards, and source-control conveniences into the daily work of database developers and administrators."] = "Axial SQL Tools 是面向社区的 SQL Server Management Studio 扩展，为数据库开发人员和管理员的日常工作提供实用流程助手、查询工具、数据导出、仪表板和源代码管理功能。",
            ["The project is developed in the open so users can inspect the code, report issues, propose improvements, review releases, and contribute fixes or documentation. Constructive participation from the SQL Server community is welcome."] = "本项目采用开放开发模式，用户可以查看代码、报告问题、提出改进、审阅版本并贡献修复或文档。欢迎 SQL Server 社区积极参与。",
            ["Contributions can include bug reports, feature ideas, pull requests, documentation updates, testing notes, and query-library improvements. If Axial SQL Tools helps your workflow, consider sharing feedback or helping make the project better for the next user."] = "贡献形式包括问题报告、功能建议、拉取请求、文档更新、测试记录和查询库改进。如果 Axial SQL Tools 对你有帮助，欢迎分享反馈并帮助项目持续完善。",
            ["License: Apache License 2.0. You may use, study, modify, and distribute the project under the terms of the license. The extension is provided as-is, without warranty."] = "许可证：Apache License 2.0。你可以按照许可证条款使用、研究、修改和分发本项目。本扩展按现状提供，不作任何保证。",
            ["Support is community-based and best-effort. Please open a GitHub issue with clear reproduction steps, logs, SSMS version, and extension version when reporting problems."] = "支持由社区尽力提供。报告问题时，请提交 GitHub Issue，并附上清晰的复现步骤、日志、SSMS 版本和扩展版本。"
        };

        public static string CurrentLanguage { get; private set; } = ChineseLanguage;
        public static bool IsChinese => CurrentLanguage.StartsWith("zh", StringComparison.OrdinalIgnoreCase);

        public static void Initialize()
        {
            SetLanguage(SettingsManager.GetUiLanguage(), false);
            EnableAutomaticLocalization();
        }

        private static void EnableAutomaticLocalization()
        {
            if (automaticLocalizationEnabled) return;
            EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, args) => Apply(sender as DependencyObject)));
            EventManager.RegisterClassHandler(typeof(UserControl), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, args) => Apply(sender as DependencyObject)));
            automaticLocalizationEnabled = true;
        }

        public static void SetLanguage(string language, bool persist = true)
        {
            CurrentLanguage = string.Equals(language, EnglishLanguage, StringComparison.OrdinalIgnoreCase)
                ? EnglishLanguage
                : ChineseLanguage;
            var culture = CultureInfo.GetCultureInfo(CurrentLanguage);
            Thread.CurrentThread.CurrentUICulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            if (persist)
            {
                SettingsManager.SaveUiLanguage(CurrentLanguage);
            }
        }

        public static string T(string text)
        {
            if (string.IsNullOrEmpty(text) || !IsChinese)
            {
                return text;
            }

            if (Chinese.TryGetValue(text, out string translated)) return translated;
            return TranslateDynamic(text);
        }

        private static string TranslateDynamic(string text)
        {
            if (text.StartsWith("Authorization failed: ", StringComparison.Ordinal)) return "授权失败：" + text.Substring(22);
            if (text.StartsWith("Import failed: ", StringComparison.Ordinal)) return "导入失败：" + text.Substring(15);
            if (text.StartsWith("Search failed: ", StringComparison.Ordinal)) return "搜索失败：" + text.Substring(15);
            if (text.StartsWith("Scripting failed: ", StringComparison.Ordinal)) return "生成脚本失败：" + text.Substring(18);
            if (text.StartsWith("The shortcut could not be applied: ", StringComparison.Ordinal)) return "无法应用快捷键：" + text.Substring(35);
            if (text.StartsWith("Something went wrong: ", StringComparison.Ordinal)) return "发生错误：" + text.Substring(22);
            if (text.StartsWith("Error loading data: ", StringComparison.Ordinal)) return "加载数据时出错：" + text.Substring(20);
            if (text.StartsWith("Server: ", StringComparison.Ordinal)) return text.Replace("Server: ", "服务器：").Replace("Database: ", "数据库：");
            if (text.StartsWith("Rows copied: ", StringComparison.Ordinal)) return text.Replace("Rows copied: ", "已复制行数：").Replace(" in ", "，耗时 ").Replace(" sec.", " 秒");
            if (text.StartsWith("Completed | Total rows copied: ", StringComparison.Ordinal)) return text.Replace("Completed | Total rows copied: ", "已完成 | 总复制行数：").Replace(" in ", "，耗时 ").Replace(" sec.", " 秒");
            if (text.StartsWith("Searching [", StringComparison.Ordinal)) return text.Replace("Searching [", "正在搜索 [");
            if (text.StartsWith("File (", StringComparison.Ordinal)) return text.Replace("File (", "文件（").Replace("):", "）：");
            return text;
        }

        public static string Format(string text, params object[] args) => string.Format(T(text), args);

        public static void Apply(DependencyObject root)
        {
            ApplyCore(root, new HashSet<DependencyObject>(new ReferenceComparer()));
        }

        private static void ApplyCore(DependencyObject root, HashSet<DependencyObject> visited)
        {
            if (root == null || !visited.Add(root)) return;
            TranslateObject(root);

            if (root is Visual || root is System.Windows.Media.Media3D.Visual3D)
            {
                int count = VisualTreeHelper.GetChildrenCount(root);
                for (int i = 0; i < count; i++) ApplyCore(VisualTreeHelper.GetChild(root, i), visited);
            }

            foreach (object child in LogicalTreeHelper.GetChildren(root))
            {
                if (child is DependencyObject dependencyObject) ApplyCore(dependencyObject, visited);
            }
        }

        private static void TranslateObject(DependencyObject item)
        {
            if (item is Window window) Translate(item, "Title", () => window.Title, v => window.Title = v);
            if (item is TextBlock textBlock) Translate(item, "Text", () => textBlock.Text, v => textBlock.Text = v);
            if (item is Run run) Translate(item, "RunText", () => run.Text, v => run.Text = v);
            if (item is ContentControl contentControl && contentControl.Content is string)
                Translate(item, "Content", () => (string)contentControl.Content, v => contentControl.Content = v);
            if (item is HeaderedContentControl headered && headered.Header is string)
                Translate(item, "Header", () => (string)headered.Header, v => headered.Header = v);
            if (item is FrameworkElement element && element.ToolTip is string)
                Translate(item, "ToolTip", () => (string)element.ToolTip, v => element.ToolTip = v);
            if (item is GridViewColumnHeader columnHeader && columnHeader.Content is string)
                Translate(item, "ColumnHeader", () => (string)columnHeader.Content, v => columnHeader.Content = v);
        }

        private static void Translate(DependencyObject item, string property, Func<string> getter, Action<string> setter)
        {
            OriginalValues originals = Originals.GetValue(item, _ => new OriginalValues());
            string current = getter();
            if (!originals.Values.TryGetValue(property, out string original)
                || !originals.LastApplied.TryGetValue(property, out string lastApplied)
                || !string.Equals(current, lastApplied, StringComparison.Ordinal))
            {
                original = current;
                originals.Values[property] = original;
            }
            string localized = IsChinese ? T(original) : original;
            setter(localized);
            originals.LastApplied[property] = localized;
        }
    }

    internal static class LocalizedMessageBox
    {
        public static MessageBoxResult Show(string messageBoxText) => MessageBox.Show(LocalizationManager.T(messageBoxText));
        public static MessageBoxResult Show(string messageBoxText, string caption) => MessageBox.Show(LocalizationManager.T(messageBoxText), LocalizationManager.T(caption));
        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button) => MessageBox.Show(LocalizationManager.T(messageBoxText), LocalizationManager.T(caption), button);
        public static MessageBoxResult Show(string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) => MessageBox.Show(LocalizationManager.T(messageBoxText), LocalizationManager.T(caption), button, icon);
        public static MessageBoxResult Show(Window owner, string messageBoxText, string caption, MessageBoxButton button, MessageBoxImage icon) => MessageBox.Show(owner, LocalizationManager.T(messageBoxText), LocalizationManager.T(caption), button, icon);
    }
}
