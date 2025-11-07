//using System.IO;
//using System.Windows;

//namespace InnoPVManagementSystem.Modules.CompareMerge.Views
//{
//    public partial class ReadOnlyDiffWindow : Window
//    {
//        public ReadOnlyDiffWindow(string leftPath, string rightPath)
//        {
//            InitializeComponent();

//            // TODO io파일은 csv변환후 읽기 가능
//            DiffView.LeftEditor.Text = File.ReadAllText(leftPath);
//            DiffView.RightEditor.Text = File.ReadAllText(rightPath);
//            DiffView.CompareNow();
//            //Title = $"파일 비교 (읽기 전용)  —  L: {System.IO.Path.GetFileName(leftPath)}  |  R: {System.IO.Path.GetFileName(rightPath)}";
//        }
//    }
//}

using ICSharpCode.AvalonEdit;
using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows;

namespace InnoPVManagementSystem.Modules.CompareMerge.Views
{
    public partial class ReadOnlyDiffWindow : Window
    {
        // 20MB 넘으면 '일부 미리보기' 모드로 열어 경고/전체 로드 버튼 제공
        private const long PreviewSizeBytes = 20L * 1024 * 1024;

        private string? _leftPath;
        private string? _rightPath;

        public string LeftFileName => _leftPath is null ? "" : System.IO.Path.GetFileName(_leftPath);
        public string RightFileName => _rightPath is null ? "" : System.IO.Path.GetFileName(_rightPath);

        // 권장: 이 생성자를 사용하세요
        public ReadOnlyDiffWindow(string leftPath, string rightPath)
        {
            _leftPath = leftPath;
            _rightPath = rightPath;
            InitializeComponent();
            DataContext = this;
        }

        // 필요시 파라미터 없는 생성자도 유지
        public ReadOnlyDiffWindow()
        {
            InitializeComponent();
            DataContext = this;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(_leftPath) || string.IsNullOrWhiteSpace(_rightPath))
                {
                    txtStatus.Text = "좌/우 파일 경로가 비어 있습니다.";
                    return;
                }

                await LoadFilesAsync(_leftPath!, _rightPath!);
                txtStatus.Text = "로드 완료";
            }
            catch (Exception ex)
            {
                txtStatus.Text = "로드 실패";
                MessageBox.Show(this, ex.Message, "로드 오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        private async Task LoadFilesAsync(string leftPath, string rightPath)
        {
            var lfi = new FileInfo(leftPath);
            var rfi = new FileInfo(rightPath);

            // 큰 파일: 일부 미리보기 + 전체 로드 버튼
            if (lfi.Length > PreviewSizeBytes || rfi.Length > PreviewSizeBytes)
            {
                InfoBanner.Visibility = Visibility.Visible;
                InfoText.Text = "파일 용량이 커서 상단 일부만 미리보기로 표시합니다. 전체 로드는 시간이 걸릴 수 있습니다.";
                btnLoadAll.Visibility = Visibility.Visible;

                LeftEditor.Text = await ReadHeadAsync(leftPath, 5000);
                RightEditor.Text = await ReadHeadAsync(rightPath, 5000);
                return;
            }

            // 일반: 전체 로드
            LeftEditor.Text = await File.ReadAllTextAsync(leftPath);
            RightEditor.Text = await File.ReadAllTextAsync(rightPath);
        }

        private static async Task<string> ReadHeadAsync(string path, int maxLines)
        {
            var sb = new StringBuilder(capacity: 1 << 20);
            int count = 0;

            // DiffKit.ReadLinesAsync 사용(스트리밍)
            await foreach (var line in InnoPVManagementSystem.Common.Services.DiffKit.ReadLinesAsync(path))
            {
                sb.AppendLine(line);
                if (++count >= maxLines) break;
            }
            return sb.ToString();
        }

        private async void OnClickLoadAll(object sender, RoutedEventArgs e)
        {
            try
            {
                btnLoadAll.IsEnabled = false;
                txtStatus.Text = "전체 로드 중...";

                if (_leftPath is null || _rightPath is null)
                    throw new InvalidOperationException("파일 경로가 초기화되지 않았습니다.");

                LeftEditor.Text = await File.ReadAllTextAsync(_leftPath);
                RightEditor.Text = await File.ReadAllTextAsync(_rightPath);

                txtStatus.Text = "전체 로드 완료";
                // 전체 로드 후 배너 숨기고 버튼 비활성화(선택)
                InfoBanner.Visibility = Visibility.Collapsed;
                btnLoadAll.Visibility = Visibility.Collapsed;
            }
            catch (Exception ex)
            {
                txtStatus.Text = "전체 로드 실패";
                MessageBox.Show(this, ex.Message, "오류", MessageBoxButton.OK, MessageBoxImage.Error);
            }
            finally
            {
                btnLoadAll.IsEnabled = true;
            }
        }

        // 외부에서 쉽게 호출하도록 helper 제공(선택)
        public static void ShowCompare(Window owner, string leftPath, string rightPath)
        {
            var w = new ReadOnlyDiffWindow(leftPath, rightPath) { Owner = owner };
            w.ShowDialog();
        }
    }
}
