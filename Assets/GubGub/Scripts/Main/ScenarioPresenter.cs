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
using UnityEngine;
using UnityEngine.Events;

namespace GubGub.Scripts.Main
{
    /// <summary>
    ///  シナリオ画面のプレゼンター
    /// </summary>
    public class ScenarioPresenter : MonoBehaviour
    {

        /// <summary>
        ///  シナリオコマンド実行クラス
        /// </summary>
        private readonly ScenarioCommandExecutor _commandExecutor = new ScenarioCommandExecutor();

        /// <summary>
        /// TSVファイルスクリプトのパーサー
        /// </summary>
        private readonly ScenarioParser _parser = new ScenarioParser();

        /// <summary>
        ///  バックログからボイス再生パスを通知されるストリーム
        /// </summary>
        private readonly Subject<string> _playVoicePathStream = new Subject<string>();

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
        private ScenarioConfigData _configData = new ScenarioConfigData();

        /// <summary>
        ///  現在参照中のスクリプト行
        /// </summary>
        private List<string> _currentLine;

        /// <summary>
        ///  オートプレイ中か
        /// </summary>
        [SerializeField]
        private bool _isAutoPlaying = true;
        
        /// <summary>
        ///  コマンド処理中にユーザー入力を止めるためのフラグ
        /// </summary>
        private bool _isWaitProcess;
        
        /// <summary>
        /// メッセージ表示中フラグ
        /// </summary>
        private bool _isProcessingShowMessage;

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
        public async Task Initialize()
        {
            await view.Initialize();
            
            _viewMediator = new ScenarioViewMediator(view, _configData);

            await InitializeLogDialog();
            InitializeCommandActions();
            
            Bind();
            AddEventListeners();
        }

        /// <summary>
        /// シナリオ、リソースの読み込みを行う
        /// </summary>
        /// <param name="loadScenarioPath"></param>
        /// <returns></returns>
        public async Task LoadScenario(string loadScenarioPath)
        {
            // シナリオの読み込み
            var scenario = await ResourceManager.LoadText(loadScenarioPath);
            ParseScenario(scenario);

            // TODO: リソースの読み込み
        }

        /// <summary>
        /// シナリオの再生を開始する
        /// </summary>
        /// <returns></returns>
        public async Task StartScenario()
        {
            Forward();

            await Task.CompletedTask;
        }

        private void Bind()
        {
            _viewMediator.onAnyClick.Subscribe(_ => OnAnyClick()).AddTo(this);

            // コマンドの終了を監視
            _commandExecutor.commandEnd.Subscribe(_ => { OnCommandEnd(); }).AddTo(this);

            // バックログからのボイス再生通知を監視
            _playVoicePathStream.Subscribe(_ => { Debug.Log(_.ToString()); }).AddTo(this);

//            _logDataStream.Subscribe(data => _logDialog.UpdateLog(data)).AddTo(this);
        }
        
        private void AddEventListeners()
        {
            _viewMediator.MessagePresenter.View.OnOptionButton = OnOptionButton;
            _viewMediator.MessagePresenter.View.OnAutoButton = OnAutoButton;
            _viewMediator.MessagePresenter.View.OnLogButton = OnLogButton;
        }

        /// <summary>
        ///  バックログダイアログを初期化する
        /// </summary>
        private async Task InitializeLogDialog()
        {
//            _logDialog = await ScenarioLogDialogView.Create();
//            await _logDialog.Initialize(View.transform);
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
        ///  シナリオを進行させる
        /// </summary>
        private void Forward()
        {
            _currentLine = _parseData.GetCurrentLineAndAdvanceNumber();

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
        ///  シナリオを自動で進行させる
        /// </summary>
        private void AutoForward()
        {
            Forward();
        }

        /// <summary>
        ///  シナリオが終了した
        /// </summary>
        private void FinishScenario()
        {
        }

        /// <summary>
        ///  行をコマンドとして処理する
        /// </summary>
        /// <param name="commandName"></param>
        /// <param name="param"></param>
        public void ProcessCommand(string commandName, List<string> param)
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
        /// <param name="voiceName"></param>
        /// <param name="speakerName"></param>
        private void PlayVoice(string voiceName, string speakerName)
        {
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

        #endregion

        #region commandAction

        /// <summary>
        /// コマンド完了時
        /// </summary>
        private void OnCommandEnd()
        {
            if (!_isProcessingShowMessage)
            {
                SetIsWaitProcessState(false);
                Forward();
            }
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

        private async Task OnWaitCommand(BaseScenarioCommand value)
        {
            var command = value as WaitCommand;
            await Task.Delay(command.waitMilliSecond);
        }

        private async Task OnImageCommand(BaseScenarioCommand value)
        {
            var command = value as ImageCommand;
            await _viewMediator.OnShowImage(command?.ImageName, command.FadeTimeMilliSecond);
        }

        private async Task OnMessageCommand(BaseScenarioCommand value)
        {
            var command = value as MessageCommand;
            PlayVoice(command?.VoiceName, command?.SpeakerName);
            
            _viewMediator.OnShowMessage(
                command?.Message, command?.SpeakerName, _configData.MessageSpeedMilliSecond);

            _isProcessingShowMessage = true;
            
            // オートプレイ処理
            // TODO: ボイスの再生待ちも条件に加える
            if (command != null)
            {
                var waitTime = Mathf.Max(
                    _configData.AutoMessageSpeedMilliSecond * command.Message.Length,
                    _configData.MinAutoWaitTimeMilliSecond);
                 Observable.Timer(TimeSpan.FromSeconds(waitTime / 1000f))
                    .Subscribe(_ =>
                {
                    if (_isAutoPlaying)
                    {
                        _isProcessingShowMessage = false;
                        Forward();
                    }
                });
            }

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
            if (_isWaitProcess)
            {
                return;
            }

            _isAutoPlaying = false;
            _isProcessingShowMessage = false;

            // メッセージ表示更新中なら、すぐに一括表示させる
            if (_viewMediator.MessagePresenter.IsMessageProcess)
            {
                _viewMediator.MessagePresenter.ShowMessageImmediate();
            }
            else
            {
                Forward();
                Debug.Log("OnAnyClick");
            }
        }

        /// <summary>
        ///  バックログボタン
        /// </summary>
        private async void OnLogButton()
        {
//            ShowBackLogDialog();
        }

        private void OnOptionButton()
        {
            Debug.Log("OnOptionButton");
        }

        private void OnAutoButton()
        {
            _isAutoPlaying = !_isAutoPlaying;
            if (_isAutoPlaying)
            {
                AutoForward();
            }
        }

        #endregion
    }
}
