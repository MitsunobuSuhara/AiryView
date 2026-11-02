using System.Text.Json;
using System.Globalization;
namespace AiryView;
public sealed class PdfToolsWindow : Window
{
    private readonly PdfDocument document;
    private readonly int page;
    private readonly Action<int> navigate;
    private readonly Func<string,Task> open;
    private readonly TextBlock status = new() { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
    private readonly StackPanel formFields = new();
    private readonly List<(string Name, FrameworkElement Input, string Original)> inputs = [];
    private readonly ListBox bookmarks = new();
    private readonly ListBox existingNotes = new() { MinHeight = 120 };
    private readonly ListBox results = new();
    private readonly TextBox search = new();
    private readonly TextBox freeText = new() { AcceptsReturn = true, Height = 90, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox note = new() { AcceptsReturn = true, Height = 65, TextWrapping = TextWrapping.Wrap };
    private readonly TextBox bookmark = new();
    private readonly TextBox x = new() { Text = "30" }, y = new() { Text = "30" }, size = new() { Text = "12" };
    private readonly TextBox signatureResults = new() { IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private bool busy;
    internal int FieldCount => inputs.Count;
    internal string StatusMessage => status.Text;
    public PdfToolsWindow(PdfDocument doc, int currentPage, Action<int> go, Func<string,Task> openPdf)
    {
        document = doc; page = currentPage; navigate = go; open = openPdf;
        Title = "AiryView — 入力・注釈・検索・署名"; Width = 800; Height = 690; MinWidth = 600; MinHeight = 450;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(14) }; Content = root;
        DockPanel.SetDock(status,Dock.Bottom); root.Children.Add(status);
        var tabs = new TabControl(); root.Children.Add(tabs);
        var form = new StackPanel();
        form.Children.Add(Label("既存のPDF入力欄に記入します。保存後も入力欄を残します。"));
        form.Children.Add(formFields); form.Children.Add(MakeButton("入力内容を別名保存", SaveFields));
        tabs.Items.Add(new TabItem { Header = "入力欄", Content = new ScrollViewer { Content = form, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var textPanel = new StackPanel();
        textPanel.Children.Add(Label($"現在の{page + 1}ページに追加します。位置はページ左上からのmmです。"));
        textPanel.Children.Add(Label("左から（mm）")); textPanel.Children.Add(x); textPanel.Children.Add(Label("上から（mm）")); textPanel.Children.Add(y);
        textPanel.Children.Add(Label("文字サイズ（pt）")); textPanel.Children.Add(size);
        textPanel.Children.Add(Label("欄のないPDFへ追加する文字（日本語対応）")); textPanel.Children.Add(freeText);
        textPanel.Children.Add(Label("付箋の注釈")); textPanel.Children.Add(note);
        textPanel.Children.Add(Label("このページへのしおり名")); textPanel.Children.Add(bookmark);
        textPanel.Children.Add(MakeButton("追加内容を別名保存", SaveAdded));
        textPanel.Children.Add(Label("既存の注釈（ダブルクリックでページ移動）"));textPanel.Children.Add(existingNotes);
        existingNotes.MouseDoubleClick += (_,_) => { if (existingNotes.SelectedItem is ListBoxItem { Tag: int p }) { navigate(p); Close(); } };
        tabs.Items.Add(new TabItem { Header = "文字・注釈の追加", Content = new ScrollViewer { Content = textPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        var find = new DockPanel(); var searchTop = new StackPanel(); DockPanel.SetDock(searchTop,Dock.Top);
        searchTop.Children.Add(Label("文字を含む全ページを検索します。スキャン画像のOCRは未対応です。")); searchTop.Children.Add(search); searchTop.Children.Add(MakeButton("検索", Search)); find.Children.Add(searchTop);find.Children.Add(results);
        results.MouseDoubleClick += (_,_) => { if (results.SelectedItem is ListBoxItem { Tag: int p }) { navigate(p); Close(); } };
        tabs.Items.Add(new TabItem { Header = "検索", Content = find });
        bookmarks.MouseDoubleClick += (_,_) => { if (bookmarks.SelectedItem is ListBoxItem { Tag: int p }) { navigate(p); Close(); } };
        tabs.Items.Add(new TabItem { Header = "しおり", Content = bookmarks });
        var signatures = new DockPanel(); var signTop = new StackPanel(); DockPanel.SetDock(signTop,Dock.Top);
        signTop.Children.Add(Label("PFX／P12証明書で署名します。検証は改ざん・署名値・Windowsの信頼証明書を調べます。オンライン失効確認は行わず、その状態を明示します。"));
        signTop.Children.Add(MakeButton("署名を確認", Verify));signTop.Children.Add(MakeButton("証明書で署名して別名保存", Sign));signatures.Children.Add(signTop); signatures.Children.Add(signatureResults);
        tabs.Items.Add(new TabItem { Header = "電子署名", Content = signatures });
        WindowTitleBar.Install(this);
        Loaded += async (_,_) => await LoadFields();
        Closing += (_,e) => { if (busy) { e.Cancel = true; status.Text = "処理の完了までお待ちください。"; } };
    }
    private static TextBlock Label(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(5,8,5,3) };
    private Button MakeButton(string text, Func<Task> action)
    {
        var button = new Button { Content = text, HorizontalAlignment = HorizontalAlignment.Left };
        button.Click += async (_,_) =>
        {
            if (busy) return; busy = true; button.IsEnabled = false;
            try { status.Text = "処理中…"; await action(); }
            catch (Exception ex) { status.Text = ex.Message; }
            finally { busy = false; button.IsEnabled = true; }
        }; return button;
    }
    private Dictionary<string,object?> Request(string operation) => new() { ["operation"] = operation, ["source"] = document.Path, ["password"] = document.Password ?? "", ["page"] = page };
    private async Task LoadFields()
    {
        busy = true;
        try
        {
            var result = await PdfHelper.RunAsync(Request("inspect"));
            foreach (var field in result.GetProperty("fields").EnumerateArray())
            {
                string name = field.GetProperty("name").GetString()!, kind = field.GetProperty("kind").GetString()!, value = field.GetProperty("value").GetString()!;
                int flags = field.GetProperty("flags").GetInt32();
                formFields.Children.Add(Label(name)); FrameworkElement input;
                if (kind == "/Btn")
                {
                    // ラジオ・押しボタンは初版では書き換えない。
                    var check = new CheckBox { IsChecked = value != "/Off" && value != "", Content = "チェック", Tag = field.GetProperty("on").GetString(), Margin = new Thickness(8) };
                    check.IsEnabled = (flags & (1 | 32768 | 65536)) == 0; input = check;
                }
                else if (kind == "/Ch")
                {
                    var choice = new ComboBox { IsEditable = (flags & 262144) != 0 };
                    foreach (var option in field.GetProperty("options").EnumerateArray()) choice.Items.Add(option.GetString());
                    choice.Text = value; input = choice; choice.IsEnabled = (flags & (1 | 2097152)) == 0;
                }
                else input = new TextBox { Text = value, MaxLength = field.GetProperty("maxLen").GetInt32(), AcceptsReturn = (flags & 4096) != 0, MinHeight = (flags & 4096) != 0 ? 60 : 30, IsEnabled = (flags & 1) == 0 };
                formFields.Children.Add(input); inputs.Add((name,input,value));
            }
            foreach (var annotation in result.GetProperty("notes").EnumerateArray())
                existingNotes.Items.Add(new ListBoxItem { Content = $"{annotation.GetProperty("page").GetInt32()+1}ページ：{annotation.GetProperty("text").GetString()}", Tag = annotation.GetProperty("page").GetInt32() });
            foreach (var mark in result.GetProperty("bookmarks").EnumerateArray())
            {
                if (!mark.GetProperty("page").TryGetInt32(out int p)) continue;
                if (p >= 0) bookmarks.Items.Add(new ListBoxItem { Content = new string(' ',mark.GetProperty("depth").GetInt32()*2) + mark.GetProperty("title").GetString() + $"  ({p+1})", Tag = p });
            }
            status.Text = result.GetProperty("xfa").GetBoolean() ? "XFAフォームはこの版では編集できません。通常のAcroFormと区別します。" : $"入力欄 {inputs.Count}件・しおり {bookmarks.Items.Count}件。しおり・検索結果はダブルクリックで移動します。";
            if (result.GetProperty("xfa").GetBoolean()) foreach(var field in inputs) field.Input.IsEnabled = false;
        }
        catch (Exception ex) { status.Text = ex.Message; }
        finally { busy = false; }
    }
    private string? Destination(string suffix, bool formOnly)
    {
        if (!(formOnly ? document.CanFill : document.CanEdit)) throw new IOException(document.HasSignatures ? "署名済みPDFは変更しません。署名のない原本を編集してください。" : "このPDFは編集が制限されています。");
        var save = new Microsoft.Win32.SaveFileDialog { Filter = "PDF|*.pdf", FileName = System.IO.Path.GetFileNameWithoutExtension(document.Path) + suffix + ".pdf" };
        return save.ShowDialog(this) == true ? save.FileName : null;
    }
    private async Task Write(Dictionary<string,object?> request, string suffix)
    {
        string? destination = Destination(suffix, request.ContainsKey("values")); if (destination == null) { status.Text = "キャンセルしました。"; return; }
        string? snapshot = null;
        try
        {
            if (document.Dirty)
            {
                snapshot = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "airy-" + Guid.NewGuid().ToString("N") + ".pdf");
                document.SaveCopy(snapshot, false); request["source"] = snapshot;
            }
            if (string.Equals(System.IO.Path.GetFullPath(destination), document.Path, StringComparison.OrdinalIgnoreCase)) throw new IOException("原本とは別の名前を指定してください。");
            request["destination"] = destination;
            await PdfHelper.RunAsync(request);
            status.Text = "保存しました。保存したPDFを新しいタブで開きます。";
            await open(destination);
        }
        finally { if (snapshot != null && File.Exists(snapshot)) File.Delete(snapshot); }
    }
    private async Task SaveFields()
    {
        var values = new Dictionary<string,string>();
        foreach (var field in inputs.Where(f => f.Input.IsEnabled))
        {
            string value = field.Input switch { TextBox t => t.Text, ComboBox c => c.Text, CheckBox c => c.IsChecked == true ? (string)c.Tag : "/Off", _ => "" };
            string original = field.Input is CheckBox && string.IsNullOrEmpty(field.Original) ? "/Off" : field.Original;
            if (value != original) values[field.Name] = value;
        }
        if (values.Count == 0) { status.Text = "変更した入力欄がありません。"; return; }
        var request = Request("write");request["values"] = values; await Write(request,"_入力済み");
    }
    private async Task SaveAdded()
    {
        if (string.IsNullOrWhiteSpace(freeText.Text + note.Text + bookmark.Text)) { status.Text = "追加する内容を入力してください。"; return; }
        double Number(TextBox box) => double.TryParse(box.Text,NumberStyles.Float,CultureInfo.InvariantCulture,out double n) && double.IsFinite(n) ? n : throw new IOException("位置・サイズは数値で入力してください。");
        double left = Number(x), top = Number(y), font = Number(size); Size mm = document.SizeMm(page);
        if (left < 0 || top < 0 || left > mm.Width - 10 || top > mm.Height - 10 || font < 5 || font > 100) throw new IOException("文字の位置をページ内、サイズを5〜100ptで指定してください。");
        var request = Request("write");request["text"] = freeText.Text;request["note"] = note.Text;request["bookmark"] = bookmark.Text;request["x"] = left;request["y"] = top;request["size"] = font;
        await Write(request,"_記入済み");
    }
    private async Task Search()
    {
        if (string.IsNullOrWhiteSpace(search.Text)) return;
        results.Items.Clear(); string term = search.Text;
        for (int i = 0; i < document.Count; i++)
        {
            string text = await Task.Run(() => document.PageText(i));
            int index = text.IndexOf(term,StringComparison.OrdinalIgnoreCase);
            if (index >= 0) results.Items.Add(new ListBoxItem { Content = $"{i+1}ページ：" + text.Substring(Math.Max(0,index-25),Math.Min(120,text.Length-Math.Max(0,index-25))).Replace("\r"," ").Replace("\n"," "), Tag = i });
        }
        status.Text = $"{results.Items.Count}ページで見つかりました。ダブルクリックで移動します。";
    }
    private async Task Verify()
    {
        var result = await PdfHelper.RunAsync(Request("verify"));var items = result.GetProperty("signatures").EnumerateArray().ToArray();
        signatureResults.Text = items.Length == 0 ? "電子署名はありません。" : string.Join("\n\n",items.Select(s => s.TryGetProperty("error",out var error) ? "検証できません：" + error.GetString() :
            $"署名：{s.GetProperty("name").GetString()}\n署名対象の改ざん：{(s.GetProperty("intact").GetBoolean() ? "検出なし" : "検出／不一致")}\n署名値：{(s.GetProperty("valid").GetBoolean() ? "一致" : "不一致")}\n証明書の信頼：{(s.GetProperty("trusted").GetBoolean() ? "信頼済み" : "確認できない")}\n署名後の変更：{(s.GetProperty("modifications_ok").ValueKind == JsonValueKind.True ? "許容範囲" : "要確認")}\n対象範囲：{s.GetProperty("coverage").GetString()}\n{s.GetProperty("revocation").GetString()}\n\n{s.GetProperty("details").GetString()}"));
        status.Text = "オフライン検証が完了しました。失効情報の未確認を含めて判断してください。";
    }
    private async Task Sign()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "証明書（PFX/P12）|*.pfx;*.p12" };
        if (dialog.ShowDialog(this) != true) return;
        string? password = TextPrompt.Ask(this,"証明書のパスワード","PFX／P12のパスワードを入力してください。",true);
        if (password == null) return;
        var request = Request("sign"); request["pfx"] = dialog.FileName; request["pfxPassword"] = password;
        await Write(request,"_署名済み");
    }
}
