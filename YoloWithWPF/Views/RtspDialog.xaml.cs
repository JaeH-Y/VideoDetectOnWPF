using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;

namespace YoloWithWPF.Views
{
    /// <summary>
    /// RtspDialog.xaml에 대한 상호 작용 논리
    /// </summary>
    public partial class RtspDialog : Window
    {
        public string ResultText { get; private set; } = "";

        public RtspDialog()
        {
            InitializeComponent();
            // 열리자마자 텍스트박스 포커스 + 전체 선택
            Loaded += (_, _) =>
            {
                InputBox.Focus();
                InputBox.SelectAll();
            };
        }

        private void Confirm_Click(object sender, RoutedEventArgs e)
        {
            ResultText = InputBox.Text.Trim();
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }
    }
}
