﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using GubGub.Scripts.Command;
using GubGub.Scripts.Data;
using GubGub.Scripts.Enum;
using GubGub.Scripts.Lib;
using GubGub.Scripts.Parser;
using UniRx;
using UniRx.Async;
using UnityEngine;

namespace GubGub.Scripts.Main
{
    /// <summary>
    ///  シナリオ画面のプレゼンター
    /// </summary>
    public class ScenarioPresenter : MonoBehaviour
    {
        /// <summary>
        /// シナリオ終了を通知するストリーム
        /// </summary>
        public IObservable<Unit> IsEndScenario => _isEndScenario;

        private readonly Subject<Unit> _isEndScenario = new Subject<Unit>();
        
        /// <summary>
        ///  シナリオコマンド実行クラス
        /// </summary>
        private readonly ScenarioCommandExecutor _commandExecutor = new ScenarioCommandExecutor();

        /// <summary>
        /// TSVファイルスクリプトのパーサー
        /// </summary>
        private readonly ScenarioParser _parser = new ScenarioParser();

        /// <summary>
        ///  コマンドに対応した処理をビューに行わせるラップクラス
        /// </summary>
        private ScenarioViewMediator _viewMediator;

        /// <summary>
        ///  パース済みのテキスト配列
        /// </summary>
        private ScenarioParseData _parseData;

        /// <summary>
        ///  シナリオ再生時の各種設定
        /// </summary>
        private readonly ScenarioConfigData _configData = new ScenarioConfigData();

        /// <summary>
        ///  現在参照中のスクリプト行
        /// </summary>
        private List<string> _currentLine;

        /// <summary>
        ///  オートプレイ中か
        /// </summary>
        [SerializeField]
        private bool isAutoPlaying = true;

        /// <summary>
        /// スキップ中か
        /// </summary>
        private bool _isSkip;
        
        /// <summary>
        /// メッセージウィンドウを閉じた状態か
        /// </summary>
        private bool _isCloseMessageWindow;
        
        /// <summary>
        ///  コマンド処理中にユーザー入力を止めるためのフラグ
        /// </summary>
        private bool _isWaitProcess;
        
        /// <summary>
        /// メッセージ表示中フラグ
        /// コマンド処理中フラグとは別に、シナリオの進行を止める
        /// </summary>
        private bool _isProcessingShowMessage;
        
        /// <summary>
        /// メッセージ表示中フラグ
        /// コマンド処理中フラグとは別に、シナリオの進行と入力処理を止める
        /// </summary>
        private bool _isProcessingShowSelection;
        
        /// <summary>
        /// メッセージ表示タイマーのDisposable
        /// </summary>
        private IDisposable _messageTimerDisposable;
        
        /// <summary>
        /// メッセージ表示用パラメータ
        /// </summary>
        private readonly ScenarioMessageData _messageData = new ScenarioMessageData();

        [SerializeField]
        public ScenarioView view;
        

        private async void Awake()
        {
            await Initialize();
        }

        /// <summary>
        /// ビューやパラメータを初期化する
        /// </summary>
        /// <returns></returns>
        private async Task Initialize()
        {
            await view.Initialize();
            
            _viewMediator = new ScenarioViewMediator(view, _configData);

            InitializeCommandActions();
            
            Bind();
            AddEventListeners();
        }
        
        /// <summary>
        /// シナリオプレイヤーを非表示にする
        /// </summary>
        public void Hide()
        {
            _viewMediator.Hide();
        }

        /// <summary>
        /// シナリオ、リソースの読み込みを行う
        /// </summary>
        /// <param name="loadScenarioPath"></param>
        /// <param name="isResourcePreload"></param>
        /// <returns></returns>
        public async Task LoadScenario(string loadScenarioPath, bool isResourcePreload)
        {
            // シナリオの読み込み
            var scenario = await ResourceManager.LoadText(
                ResourceLoadSetting.ScenarioResourcePrefix + loadScenarioPath);
            ParseScenario(scenario);

            // リソースの事前読み込み
            if (isResourcePreload)
            {
                await ResourcePreload();
            }
        }

        /// <summary>
        /// アセットバンドルの事前読み込みを行う
        /// </summary>
        /// <returns></returns>
        private async UniTask ResourcePreload()
        {
            await ResourceManager.StartBulkLoad(_parser.GetResourceList());
        }

        /// <summary>
        /// シナリオの再生を開始する
        /// </summary>
        /// <returns></returns>
        public async Task StartScenario()
        {
            _viewMediator.ResetView();
            _viewMediator.Show();
            Forward();
            
            await Task.CompletedTask;
        }

        private void Bind()
        {
            _viewMediator.onAnyClick.Subscribe(_ => OnAnyClick()).AddTo(this);

            // コマンドの終了を監視
            _commandExecutor.commandEnd.Subscribe(_ => { OnCommandEnd(); }).AddTo(this);

            // バックログからのボイス再生通知を監視
            _viewMediator.BackLogPresenter.PlayVoiceStream.Subscribe(PlayVoice);

            // メッセージの表示完了を監視
            _viewMediator.MessagePresenter.IsEndMessage.Subscribe(_ => OnEndMessage());
            
            // 選択肢のクリックを監視
            _viewMediator.SelectionPresenter.onSelect.Subscribe( OnSelectionClick);
        }

        private void AddEventListeners()
        {
            _viewMediator.MessagePresenter.View.OnOptionButton = OnOptionButton;
            _viewMediator.MessagePresenter.View.OnAutoButton = OnAutoButton;
            _viewMediator.MessagePresenter.View.OnLogButton = OnLogButton;
            _viewMediator.MessagePresenter.View.OnSkipButton = OnSkipButton;
            _viewMediator.MessagePresenter.View.OnCloseButton= OnCloseButton;

            _viewMediator.BackLogPresenter.onTouchDimmer = HideBackLog;
        }

        /// <summary>
        /// 各コマンドに対応したメソッドをコマンド実行クラスに登録する
        /// </summary>
        private void InitializeCommandActions()
        {
            _commandExecutor.AddCommand(EScenarioCommandType.Message, OnMessageCommand);
            _commandExecutor.AddCommand(EScenarioCommandType.ShowWindow, OnShowWindowCommand);
            _commandExecutor.AddCommand(EScenarioCommandType.Stand, OnStandCommand);
            _commandExecutor.AddCommand(EScenarioCommandType.Image, OnImageCommand);
            _commandExecutor.AddCommand(EScenarioCommandType.Wait, OnWaitCommand);
            _commandExecutor.AddCommand(EScenarioCommandType.FadeOut, OnFadeOutCommand);
            _commandExecutor.AddCommand(EScenarioCommandType.FadeIn, OnFadeInCommand);
            _commandExecutor.AddCommand(EScenarioCommandType.Clear, OnClearCommand);
            _commandExecutor.AddCommand(EScenarioCommandType.Se, OnSeCommand);
            _commandExecutor.AddCommand(EScenarioCommandType.Bgm, OnBgmCommand);
            _commandExecutor.AddCommand(EScenarioCommandType.Jump, OnJumpCommand);
            _commandExecutor.AddCommand(EScenarioCommandType.Label, OnLabelCommand);
            _commandExecutor.AddCommand(EScenarioCommandType.Selection, OnSelectionCommand);
        }

        /// <summary>
        /// 読み込んだテキストアセットをシナリオデータにパースする
        /// </summary>
        /// <param name="scenario"></param>
        private void ParseScenario(TextAsset scenario)
        {
            List<List<string>> list = _parser.ParseScript("", scenario.text);

            _parseData = new ScenarioParseData(list);
        }

        #region private method

        /// <summary>
        /// 行番号を進め、シナリオを進行させる
        /// </summary>
        /// <param name="jumpLine">ジャンプする際の行データ</param>
        private void ForwardNextLine(List<string> jumpLine = null)
        {
            _parseData.AdvanceLineNumber();
            Forward(jumpLine);
        }
        
        /// <summary>
        /// シナリオを進行させる
        /// </summary>
        /// <param name="jumpLine">ジャンプする際の行データ</param>
        private void Forward(List<string> jumpLine = null)
        {
            _currentLine = jumpLine ?? _parseData.GetCurrentLine();

            if (_currentLine == null || _currentLine.Count <= 0)
            {
                FinishScenario();
                return;
            }

            if (IsValidLine(_currentLine))
            {
                if (_currentLine[0] != null && _currentLine[0].Length > 0)
                {
                    ProcessCommand(_currentLine[0], _currentLine.Skip(1).ToList());
                }
                else
                {
                    ProcessMessage(_currentLine.Skip(1).ToList());
                }
            }
        }

        /// <summary>
        ///  シナリオが終了した
        /// </summary>
        private void FinishScenario()
        {
            SoundManager.StopSound();
            
            _isEndScenario.OnNext(Unit.Default);
        }

        /// <summary>
        ///  行をコマンドとして処理する
        /// </summary>
        /// <param name="commandName"></param>
        /// <param name="param"></param>
        private void ProcessCommand(string commandName, List<string> param)
        {
            SetIsWaitProcessState(true);
            _commandExecutor.ProcessCommand(commandName, param);
        }
        
        /// <summary>
        ///  メッセージコマンドを処理する
        /// </summary>
        /// <param name="param"></param>
        private void ProcessMessage(List<string> param)
        {
            _commandExecutor.ProcessCommand(EScenarioCommandType.Message.ToString(), param);
        }

        /// <summary>
        ///  ボイスを再生する
        /// </summary>
        /// <param name="voicePath"></param>
        private void PlayVoice(string voicePath)
        {
            Debug.Log(voicePath);
        }

        /// <summary>
        /// 引数のテキストリストが有効なコマンド行かどうか調べる
        /// 中身に空の文字列しかない場合、無効なコマンドとする
        /// </summary>
        /// <param name="line"></param>
        /// <returns></returns>
        private static bool IsValidLine(IEnumerable<string> line)
        {
            return line.Any(value => !string.IsNullOrEmpty(value));
        }

        /// <summary>
        ///  ユーザー入力を止めるための処理待ちフラグを設定する
        /// </summary>
        /// <param name="value"></param>
        private void SetIsWaitProcessState(bool value)
        {
            _isWaitProcess = value;
        }
        
        /// <summary>
        /// メッセージ表示完了時、一定のディレイ後、オートやスキップ中なら次に進む
        /// </summary>
        private void OnEndMessage()
        {
            _messageTimerDisposable?.Dispose();
            
            _messageTimerDisposable = Observable
                .Timer(TimeSpan.FromMilliseconds(GetMinMessageWaitTimeMilliSecond()))
                .Subscribe(_ =>
                {
                    _isProcessingShowMessage = false;
                    
                    if ((isAutoPlaying || _isSkip) && !_isProcessingShowSelection)
                    {
                        ForwardNextLine();
                    }
                }).AddTo(this);
        }
        
        /// <summary>
        /// コマンド完了時
        /// </summary>
        private void OnCommandEnd()
        {
            // メッセージコマンドは即終了状態になるが、クリック待ちを行うため、次の行には進まない
            // 選択肢コマンドも同様
            if (!_isProcessingShowMessage && !_isProcessingShowSelection)
            {
                SetIsWaitProcessState(false);

                ForwardNextLine();
            }
        }

        /// <summary>
        /// メッセージの一文字あたりの表示速度を取得する
        /// </summary>
        /// <returns></returns>
        private int GetMessageSpeedMilliSecond()
        {
            if (_isSkip)
            {
                return _configData.SkipMessageSpeedMilliSecond;
            }
            if (isAutoPlaying)
            {
                return _configData.AutoMessageSpeedMilliSecond;
            }

            return _configData.MessageSpeedMilliSecond;
        }
        
        /// <summary>
        /// メッセージ表示完了タイマーのウェイト時間を取得する
        /// </summary>
        /// <returns></returns>
        private int GetMinMessageWaitTimeMilliSecond()
        {
            return _isSkip ?
                _configData.MinSkipWaitTimeMilliSecond :
                _configData.MinAutoWaitTimeMilliSecond;
        }

        /// <summary>
        /// バックログを非表示にする
        /// </summary>
        private void HideBackLog()
        {
            _viewMediator.HideScenarioLog();
        }
        
        /// <summary>
        /// 指定のラベル名に遷移して、シナリオを進める
        /// </summary>
        /// <param name="labelName"></param>
        private void JumpToLabelAndForward(string labelName)
        {
            if (string.IsNullOrEmpty(labelName))
            {
                return;
            }
            
            var line = _parseData.GetLineForJumpToLabel(labelName);
            Forward(line);
        }
        
        /// <summary>
        /// 選択肢ビューを選択した
        /// </summary>
        private void OnSelectionClick(string labelName)
        {
            _isProcessingShowSelection = false;
            
            _viewMediator.SelectionPresenter.Clear();
            JumpToLabelAndForward(labelName);
        }
        
        /// <summary>
        /// メッセージウィンドウの表示状態を変更する
        /// </summary>
        private void ChangeWindowCloseState(bool isCloseWindow)
        {
            _isCloseMessageWindow = isCloseWindow;
            _viewMediator.ChangeMessageWindowVisible(!_isCloseMessageWindow);
        }
        
        #endregion

        #region commandAction
        
        private async Task OnLabelCommand(BaseScenarioCommand value)
        {
            // 何もしない
            await Task.CompletedTask;
        }

        private async Task OnSelectionCommand(BaseScenarioCommand value)
        {
            var command = value as SelectionCommand;

            // 次の行も選択肢コマンドでなければ、選択肢表示中フラグを有効にし、
            // 以降のコマンドに進まないようにする
            if (!_parseData.GetIsMatchNextLineCommandName(
                command.CommandType.GetName()))
            {
                _isProcessingShowSelection = true;
            }
            
            _viewMediator.SelectionPresenter.AddSelection(command);
            await Task.CompletedTask;
        }
        
        private async Task OnJumpCommand(BaseScenarioCommand value)
        {
            var command = value as JumpCommand;
            SetIsWaitProcessState(false);

            JumpToLabelAndForward(command.LabelName);
        }
    
        private async Task OnClearCommand(BaseScenarioCommand value)
        {
            var command = value as ClearCommand;
            await _viewMediator.Clear(command);
        }

        private async Task OnFadeInCommand(BaseScenarioCommand value)
        {
            var command = value as FadeInCommand;
            await _viewMediator.FadeIn(command);
        }

        private async Task OnFadeOutCommand(BaseScenarioCommand value)
        {
            var command = value as FadeOutCommand;
            await _viewMediator.FadeOut(command);
        }

        private async Task OnShowWindowCommand(BaseScenarioCommand value)
        {
            var command = value as ShowWindowCommand;
            _viewMediator.ShowWindow(command);
            await Task.CompletedTask;
        }

        private async Task OnBgmCommand(BaseScenarioCommand value)
        {
            var command = value as BgmCommand;
            SoundManager.PlayBgm(ResourceLoadSetting.BgmResourcePrefix + command?.FileName);
            await Task.CompletedTask;
        }
        
        private async Task OnSeCommand(BaseScenarioCommand value)
        {
            var command = value as SeCommand;
            SoundManager.PlaySe(ResourceLoadSetting.SeResourcePrefix + command?.FileName);
            await Task.CompletedTask;
        }
        
        private async Task OnWaitCommand(BaseScenarioCommand value)
        {
            await Task.Delay(
                (value is WaitCommand command)? command.waitMilliSecond : 
                WaitCommand.DefaultWaitMilliSecond);
        }

        private async Task OnImageCommand(BaseScenarioCommand value)
        {
            var command = value as ImageCommand;
            await _viewMediator.OnShowImage(command?.ImageName,
                command?.FadeTimeMilliSecond ?? ImageCommand.DefaultFadeTimeMilliSecond);
        }

        private async Task OnMessageCommand(BaseScenarioCommand value)
        {
            var command = value as MessageCommand;
            
            PlayVoice(command?.VoiceName);
            
            // メッセージ表示開始
            _messageData.SetParam(command?.Message, command?.SpeakerName,
                GetMessageSpeedMilliSecond(), _isSkip);
            _viewMediator.OnShowMessage(_messageData);

            _viewMediator.AddScenarioLog(command);

            _isProcessingShowMessage = true;
            _messageTimerDisposable?.Dispose();
            
            await Task.CompletedTask;
        }
        
        private async Task OnStandCommand(BaseScenarioCommand value)
        {
            var command = value as StandCommand;
            await _viewMediator.ShowStand(command);
        }
        
        #endregion

        #region userInput method

        /// <summary>
        ///  画面中をクリックした
        /// </summary>
        private void OnAnyClick()
        {
            var tempIsSkip = _isSkip;
            
            // ユーザー操作を止めている間も、オートとスキップ状態の解除は行う
            isAutoPlaying = false;
            _viewMediator.MessagePresenter.SetAutoButtonState(false);
            
            _isSkip = false;
            _viewMediator.MessagePresenter.SetSkipButtonState(false);

            // メッセージウィンドウがクローズ中なら、クローズの解除だけ行う
            if (_isCloseMessageWindow)
            {
                ChangeWindowCloseState(false);
                return;
            }
            
            if (_isWaitProcess || _isProcessingShowSelection)
            {
                return;
            }

            _isProcessingShowMessage = false;
            _messageTimerDisposable?.Dispose();

            // スキップ中だったなら表示を止めるだけにして、次には進まない
            if (tempIsSkip)
            {
                return;
            }

            // メッセージ表示更新中なら、すぐに一括表示させる
            if (_viewMediator.MessagePresenter.IsMessageProcess)
            {
                _viewMediator.MessagePresenter.ShowMessageImmediate();
            }
            else
            {
                ForwardNextLine();
            }
        }
        
        /// <summary>
        ///  バックログボタン
        /// </summary>
        private void OnLogButton()
        {
            _viewMediator.ShowScenarioLog();
        }

        /// <summary>
        /// オプションボタン
        /// </summary>
        private void OnOptionButton()
        {
            Debug.Log("OnOptionButton");
        }

        /// <summary>
        /// オートプレイボタン
        /// </summary>
        /// <param name="isAuto"></param>
        private void OnAutoButton(bool isAuto)
        {
            isAutoPlaying = isAuto;
            
            // メッセージ表示タイマー終了後にオートプレイになった場合は、すぐに進める
            if (isAutoPlaying && !_isProcessingShowMessage && !_isProcessingShowSelection)
            {
                ForwardNextLine();
            }
        }

        /// <summary>
        /// スキップボタン
        /// </summary>
        /// <param name="isSkip"></param>
        private void OnSkipButton(bool isSkip)
        {
            _isSkip = isSkip;
            
            if (isSkip && !_isWaitProcess)
            {
                // メッセージ表示更新中なら、スキップ表示に変更させる
                if (_viewMediator.MessagePresenter.IsMessageProcess)
                {
                    _viewMediator.MessagePresenter.EnableMessageSkip();
                }
                else if(!_isProcessingShowSelection)
                {
                    // メッセージを表示しきっている状態なので、すぐ次に進める
                    _isProcessingShowMessage = false;
                    ForwardNextLine();
                }
            }
        }

        /// <summary>
        /// クローズボタン
        /// </summary>
        private void OnCloseButton()
        {
            _isSkip = false;
            isAutoPlaying = false;
            
            ChangeWindowCloseState(true);
        }

        #endregion
    }
}
