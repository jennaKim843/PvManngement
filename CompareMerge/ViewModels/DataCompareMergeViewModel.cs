// Modules/CompareMerge/ViewModels/DataCompareMergeViewModel.cs
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using InnoPVManagementSystem.Common.Services;
using Microsoft.Win32;

namespace InnoPVManagementSystem.Modules.CompareMerge.ViewModels
{
    /// <summary>
    /// Compare/Merge 화면용 ViewModel
    /// - 비교(대용량 자동 전환), 적용(머지/내보내기 훅), 파일/폴더 선택 커맨드 포함
    /// - IsComparing으로 진행 오버레이/버튼활성 제어
    /// </summary>
    public class DataCompareMergeViewModel : INotifyPropertyChanged
    {
        // ====== 임계값 (필요시 설정으로 분리) ======
        private const long BigFileSizeBytes = 200L * 1024 * 1024; // 200MB
        private const long BigLineCountThreshold = 5_000_000;          // 500만 라인

        private CancellationTokenSource? _cts;

        public DataCompareMergeViewModel()
        {
            // 파일 비교/적용
            CompareFileCommand = new RelayCommand(async _ => await CompareFileAsync(), _ => !IsComparing && CanCompare);
            ApplyCommand = new RelayCommand(async _ => await ApplyAsync(), _ => !IsComparing && CanApply);

            // 파일/폴더 선택
            SelectLeftFileCommand = new RelayCommand(_ => SelectLeftFile(), _ => !IsComparing);
            SelectRightFileCommand = new RelayCommand(_ => SelectRightFile(), _ => !IsComparing);
            SelectFolderCommand = new RelayCommand(_ => SelectFolder(), _ => !IsComparing);

            // 취소
            CancelCommand = new RelayCommand(_ => _cts?.Cancel(), _ => IsComparing);

            TargetFolderList = new ObservableCollection<string>();
        }

        // ====== 바인딩 속성 ======
        private string? _leftFilePath;
        public string? LeftFilePath
        {
            get => _leftFilePath;
            set { _leftFilePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanCompare)); Invalidate(); }
        }

        private string? _rightFilePath;
        public string? RightFilePath
        {
            get => _rightFilePath;
            set { _rightFilePath = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanCompare)); Invalidate(); }
        }

        private string? _baseFolderPath;
        public string? BaseFolderPath
        {
            get => _baseFolderPath;
            set { _baseFolderPath = value; OnPropertyChanged(); RefreshTargetFolderList(); }
        }

        private bool _isComparing;
        public bool IsComparing
        {
            get => _isComparing;
            set { _isComparing = value; OnPropertyChanged(); Invalidate(); }
        }

        private string _status = "";
        public string Status
        {
            get => _status;
            set { _status = value; OnPropertyChanged(); }
        }

        // 비교 결과 요약
        private long _commonCount;
        public long CommonCount { get => _commonCount; set { _commonCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanApply)); Invalidate(); } }

        private long _onlyLeftCount;
        public long OnlyLeftCount { get => _onlyLeftCount; set { _onlyLeftCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanApply)); Invalidate(); } }

        private long _onlyRightCount;
        public long OnlyRightCount { get => _onlyRightCount; set { _onlyRightCount = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanApply)); Invalidate(); } }

        // 샘플 미리보기 경로(대용량 비교 시 제공)
        private string? _leftOnlySamplePath;
        public string? LeftOnlySamplePath { get => _leftOnlySamplePath; set { _leftOnlySamplePath = value; OnPropertyChanged(); } }

        private string? _rightOnlySamplePath;
        public string? RightOnlySamplePath { get => _rightOnlySamplePath; set { _rightOnlySamplePath = value; OnPropertyChanged(); } }

        private string? _commonSamplePath;
        public string? CommonSamplePath { get => _commonSamplePath; set { _commonSamplePath = value; OnPropertyChanged(); } }

        // 폴더 내부 하위 폴더 리스트(옵션)
        public ObservableCollection<string> TargetFolderList { get; }

        // ====== 계산 속성 ======
        public bool CanCompare => File.Exists(LeftFilePath ?? "") && File.Exists(RightFilePath ?? "");
        public bool CanApply => (OnlyLeftCount > 0 || OnlyRightCount > 0 || CommonCount > 0);

        // ====== 커맨드 ======
        public ICommand CompareFileCommand { get; }
        public ICommand ApplyCommand { get; }
        public ICommand SelectLeftFileCommand { get; }
        public ICommand SelectRightFileCommand { get; }
        public ICommand SelectFolderCommand { get; }
        public ICommand CancelCommand { get; }

        private void Invalidate()
        {
            CommandManager.InvalidateRequerySuggested();
        }

        // ====== 비교 실행 ======
        public async Task CompareFileAsync()
        {
            if (!CanCompare)
            {
                Status = "좌/우 파일 경로를 확인하세요.";
                return;
            }

            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            var ct = _cts.Token;

            try
            {
                IsComparing = true;
                Status = "비교 준비중...";

                var lfi = new FileInfo(LeftFilePath!);
                var rfi = new FileInfo(RightFilePath!);

                var bigBySize = (lfi.Length >= BigFileSizeBytes) || (rfi.Length >= BigFileSizeBytes);

                long? leftLines = null, rightLines = null;
                if (!bigBySize)
                {
                    Status = "라인 수 계산중(스트리밍)...";
                    var t1 = DiffKit.CountLinesAsync(LeftFilePath!, ct);
                    var t2 = DiffKit.CountLinesAsync(RightFilePath!, ct);
                    await Task.WhenAll(t1, t2);
                    leftLines = t1.Result;
                    rightLines = t2.Result;
                }

                var isBig = bigBySize
                            || (leftLines.HasValue && leftLines.Value >= BigLineCountThreshold)
                            || (rightLines.HasValue && rightLines.Value >= BigLineCountThreshold);

                if (isBig)
                {
                    Status = "대용량 모드(외부 정렬)로 비교중...";
                    var res = await DiffKit.CompareFilesExternalAsync(LeftFilePath!, RightFilePath!, ct: ct);

                    UpdateSummary(res.CommonCount, res.OnlyLeftCount, res.OnlyRightCount,
                                  res.LeftOnlySamplePath, res.RightOnlySamplePath, res.CommonSamplePath);

                    Status = $"완료: 공통 {CommonCount:N0}, 좌만 {OnlyLeftCount:N0}, 우만 {OnlyRightCount:N0}";
                }
                else
                {
                    Status = "경량 모드(인메모리)로 비교중...";
                    var res = await DiffKit.CompareFilesInMemoryAsync(LeftFilePath!, RightFilePath!, ct);

                    UpdateSummary(res.CommonCount, res.OnlyLeftCount, res.OnlyRightCount,
                                  null, null, null);

                    Status = $"완료: 공통 {CommonCount:N0}, 좌만 {OnlyLeftCount:N0}, 우만 {OnlyRightCount:N0}";
                }
            }
            catch (OperationCanceledException)
            {
                Status = "취소됨.";
            }
            catch (Exception ex)
            {
                Status = "오류: " + ex.Message;
            }
            finally
            {
                IsComparing = false;
            }
        }

        private void UpdateSummary(
            long common, long onlyLeft, long onlyRight,
            string? leftOnlySamplePath, string? rightOnlySamplePath, string? commonSamplePath)
        {
            CommonCount = common;
            OnlyLeftCount = onlyLeft;
            OnlyRightCount = onlyRight;
            LeftOnlySamplePath = leftOnlySamplePath;
            RightOnlySamplePath = rightOnlySamplePath;
            CommonSamplePath = commonSamplePath;
        }

        // ====== 적용(머지/반영) ======
        private async Task ApplyAsync()
        {
            if (!CanApply)
            {
                Status = "적용할 변경이 없습니다.";
                return;
            }

            // TODO: 여기서 실제 반영 규칙을 연결하세요.
            // - OnlyLeft / OnlyRight / Common 샘플 파일을 열어 사용자 확인 후 반영
            // - 혹은 자동 규칙으로 결과 파일 생성
            // 샘플: 좌/우 중 우선순위 우측을 기준으로 결과 파일 생성(데모)
            try
            {
                IsComparing = true;
                Status = "변경 적용중...";

                // 간단 예시: 우측 파일을 결과로 내보내기
                var dst = MakeExportPath(RightFilePath!, suffix: "_APPLIED");
                await File.WriteAllBytesAsync(dst, await File.ReadAllBytesAsync(RightFilePath!));
                Status = $"적용 완료: {dst}";
            }
            catch (Exception ex)
            {
                Status = "적용 실패: " + ex.Message;
            }
            finally
            {
                IsComparing = false;
            }

            static string MakeExportPath(string src, string suffix)
            {
                var dir = Path.GetDirectoryName(src)!;
                var name = Path.GetFileNameWithoutExtension(src);
                var ext = Path.GetExtension(src);
                return Path.Combine(dir, $"{name}{suffix}{ext}");
            }
        }

        // ====== 파일/폴더 선택 ======
        private void SelectLeftFile()
        {
            var dlg = new OpenFileDialog
            {
                Title = "좌측 파일 선택",
                Filter = "모든 파일|*.*",
                CheckFileExists = true
            };
            if (!string.IsNullOrEmpty(LeftFilePath) && File.Exists(LeftFilePath))
                dlg.InitialDirectory = Path.GetDirectoryName(LeftFilePath);

            if (dlg.ShowDialog() == true)
            {
                LeftFilePath = dlg.FileName;
            }
        }

        private void SelectRightFile()
        {
            var dlg = new OpenFileDialog
            {
                Title = "우측 파일 선택",
                Filter = "모든 파일|*.*",
                CheckFileExists = true
            };
            if (!string.IsNullOrEmpty(RightFilePath) && File.Exists(RightFilePath))
                dlg.InitialDirectory = Path.GetDirectoryName(RightFilePath);

            if (dlg.ShowDialog() == true)
            {
                RightFilePath = dlg.FileName;
            }
        }

        /// <summary>
        /// 폴더 선택: OpenFileDialog 트릭(아무 파일 선택 시 폴더만 추출)
        /// </summary>
        private void SelectFolder()
        {
            var dialog = new OpenFileDialog
            {
                Title = "폴더 선택 (아무 파일이나 선택하면 해당 폴더가 선택됩니다)",
                CheckFileExists = false,
                CheckPathExists = true,
                ValidateNames = false,
                FileName = "Folder Selection.",
                DefaultExt = "folder",
                Filter = "폴더 선택|*.folder"
            };

            if (!string.IsNullOrEmpty(BaseFolderPath) && Directory.Exists(BaseFolderPath))
                dialog.InitialDirectory = BaseFolderPath;

            if (dialog.ShowDialog() == true)
            {
                var selectedPath = Path.GetDirectoryName(dialog.FileName);
                if (!string.IsNullOrEmpty(selectedPath))
                    BaseFolderPath = selectedPath;
            }
        }

        private void RefreshTargetFolderList()
        {
            TargetFolderList.Clear();

            if (string.IsNullOrWhiteSpace(BaseFolderPath) || !Directory.Exists(BaseFolderPath))
                return;

            try
            {
                foreach (var dir in Directory.GetDirectories(BaseFolderPath))
                    TargetFolderList.Add(dir);
            }
            catch (Exception ex)
            {
                // 권한/경합 등 예외는 상태로만 표시
                Status = "하위 폴더 조회 실패: " + ex.Message;
            }
        }

        // ====== INotifyPropertyChanged ======
        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// WPF RequerySuggested 연동형 RelayCommand (CanExecute 자동 갱신)
    /// </summary>
    public sealed class RelayCommand : ICommand
    {
        private readonly Func<object?, Task>? _asyncExec;
        private readonly Action<object?>? _exec;
        private readonly Func<object?, bool>? _can;

        public RelayCommand(Func<object?, Task> exec, Func<object?, bool>? can = null)
        { _asyncExec = exec; _can = can; }

        public RelayCommand(Action<object?> exec, Func<object?, bool>? can = null)
        { _exec = exec; _can = can; }

        public bool CanExecute(object? parameter) => _can?.Invoke(parameter) ?? true;

        public async void Execute(object? parameter)
        {
            if (_asyncExec is not null) { await _asyncExec(parameter); return; }
            _exec?.Invoke(parameter);
        }

        public event EventHandler? CanExecuteChanged
        {
            add { CommandManager.RequerySuggested += value; }
            remove { CommandManager.RequerySuggested -= value; }
        }
    }
}
