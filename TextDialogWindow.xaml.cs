using System.Windows;

namespace DesktopIconChanger;

/// <summary>可滚动的纯文本对话框 —— 用于显示许可协议等长文本。</summary>
public partial class TextDialogWindow : Window
{
    public TextDialogWindow(string content, string title)
    {
        InitializeComponent();
        TitleBarTheme.Attach(this);
        Title = title;
        txtTitle.Text = title;
        txtContent.Text = content;
    }

    private void BtnOk_Click(object sender, RoutedEventArgs e) => Close();
}
