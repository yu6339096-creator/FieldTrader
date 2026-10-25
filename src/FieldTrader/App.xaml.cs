using System.IO;
using System.Windows;
namespace FieldTrader;
public partial class App : Application {
 private SessionState? session;
 protected override async void OnStartup(StartupEventArgs e) { base.OnStartup(e); ShutdownMode=ShutdownMode.OnMainWindowClose; try {
  if(e.Args.Length==2&&e.Args[0]=="--auto-buy-ui-check"){Market.AutoBuyPanel.ValidateUi(e.Args[1]);Shutdown();return;}
  if(e.Args.Length==3&&e.Args[0]=="--profit-advice-preview"){Market.ProfitRecommendationPanel.Preview(e.Args[1],e.Args[2]);Shutdown();return;}
  if(e.Args.Length==2&&e.Args[0]=="--key-settings-preview"){Market.KeyProcurementPanel.Preview(e.Args[1]);Shutdown();return;}
  if(e.Args.Length==2&&e.Args[0]=="--native-mouse-check"){await Market.NativeMouseValidation.Run(e.Args[1]);Shutdown();return;}
  if(e.Args.Length==4&&e.Args[0]=="--larry-local-test"){await Market.LarryScreenshotLab.Run(e.Args[1],e.Args[2],e.Args[3]);Shutdown();return;}
  if(e.Args.Length==4&&e.Args[0]=="--larry-local-categories"){await Market.LarryScreenshotLab.Run(e.Args[1],e.Args[2],e.Args[3],categoriesOnly:true);Shutdown();return;}
  if(e.Args.Length==4&&e.Args[0]=="--larry-local-simulated"){await Market.LarryScreenshotLab.Run(e.Args[1],e.Args[2],e.Args[3],FieldTrader.Navigation.ExecutionMode.Simulated);Shutdown();return;}
  if(e.Args.Length==4&&e.Args[0]=="--larry-replay"){await Market.LarryValidation.Replay(e.Args[1],e.Args[2],e.Args[3]);Shutdown();return;}
  if(e.Args.Length==2&&e.Args[0]=="--visual-navigation-cancel-test"){await Market.VisualNavigationIntegrationValidation.CancelBinding(e.Args[1]);Shutdown();return;}
  if(e.Args.Length==3&&e.Args[0]=="--visual-navigation-overlays"){Market.VisualNavigationIntegrationValidation.RenderReadOnly(e.Args[1],e.Args[2]);Shutdown();return;}
  if(e.Args.Length==2&&e.Args[0]=="--visual-navigation-integration-test"){await Market.VisualNavigationIntegrationValidation.Run(e.Args[1]);Shutdown();return;}
  if(e.Args.Length==2&&e.Args[0]=="--visual-navigation-local-test"){await FieldTrader.NavigationLab.NavigationLabHost.RunIntegratedSuite(e.Args[1]);Shutdown();return;}
  if(e.Args.Length==4&&e.Args[0]=="--visual-navigation-replay"){await Market.VisualNavigationValidation.Replay(e.Args[1],e.Args[2],e.Args[3]);Shutdown();return;}
  if(e.Args.Length==2&&e.Args[0]=="--market-ab-test"){await Task.Run(()=>Market.MarketStageValidation.Run(e.Args[1]));Shutdown();return;}
  if(e.Args.Length==2&&e.Args[0]=="--stage-cd-test"){await Task.Run(()=>Market.MarketCdValidation.Run(e.Args[1]));Shutdown();return;}
  if(e.Args.Length==4&&e.Args[0]=="--cd-vision"){await Task.Run(()=>Market.MarketCdValidation.Replay(e.Args[1],e.Args[2],e.Args[3]));Shutdown();return;}
  if(e.Args.Length==4&&e.Args[0]=="--market-vision"){await Task.Run(()=>Market.MarketStageValidation.AnalyzeFolder(e.Args[1],e.Args[2],e.Args[3]));Shutdown();return;}
  if(e.Args.Length==4&&e.Args[0]=="--market-integration-test"){await Market.MarketIntegrationValidation.Run(e.Args[1],e.Args[2],e.Args[3]);Shutdown();return;}
  if(e.Args.Length==3&&e.Args[0]=="--market-ui-preview"){await Market.MarketIntegrationValidation.Preview(e.Args[1],e.Args[2]);Shutdown();return;}
  if(e.Args.Length>=2&&e.Args[0]=="--cable-bundle-test"){VoiceCableValidation.Run(e.Args[1]);Shutdown();return;}
  if(e.Args.Length==1&&e.Args[0]=="--has-vbcable"){Shutdown(VoiceCableInstaller.IsInstalled()?0:3);return;}
  if(e.Args.Length>=2&&e.Args[0]=="--microphone-mix-test"){VoiceMixValidation.Run(e.Args[1]);Shutdown();return;}
  if(e.Args.Length>=2&&e.Args[0]=="--microphone-probe"){await VoiceMixValidation.Probe(e.Args[1]);Shutdown();return;}
  if(e.Args.Length>=2&&e.Args[0]=="--ptt-test"){await VoicePlaybackValidation.Run(e.Args[1]);Shutdown();return;}
  if(e.Args.Length>=2&&e.Args[0]=="--voice-library-test"){VoiceLibraryValidation.Run(e.Args[1]);Shutdown();return;}
  if(e.Args.Length>=2&&e.Args[0]=="--voiceboard-test"){await VoiceBoardValidation.Run(e.Args[1]);Shutdown();return;}
  if(e.Args.Length>=2&&e.Args[0]=="--gain-test"){await Task.Run(()=>GainValidation.Run(e.Args[1]));Shutdown();return;}
  if(e.Args.Length>=2&&e.Args[0]=="--pipeline-test"){await PipelineValidation.Run(e.Args[1]);Shutdown();return;}
  if(e.Args.Length>=2&&e.Args[0]=="--outdoor-test"){await Task.Run(()=>OutdoorValidation.Run(e.Args[1]));Shutdown();return;}
  if(e.Args.Length>=2&&e.Args[0]=="--interference-test"){await Task.Run(()=>InterferenceValidation.Run(e.Args[1]));Shutdown();return;}
  if(e.Args.Length>=2&&e.Args[0]=="--coverage-audit"){await Task.Run(()=>LibraryCoverageAudit.Run(e.Args[1]));Shutdown();return;}
  if(e.Args.Length>=2&&e.Args[0]=="--video-library-test"){await Task.Run(()=>VideoLibraryValidation.Run(e.Args[1]));Shutdown();return;}
  if(e.Args.Length>=3&&e.Args[0]=="--waveform-audit"){await Task.Run(()=>WaveformAudit.Run(e.Args[1],e.Args[2]));Shutdown();return;}
  if(e.Args.Length>=4&&e.Args[0]=="--paired-recording-audit"){await Task.Run(()=>PairedRecordingAudit.Run(e.Args[1],e.Args[2],e.Args[3]));Shutdown();return;}
  if(e.Args.Length>=4&&e.Args[0]=="--background-audit"){await Task.Run(()=>BackgroundMatchAudit.Run(e.Args[1],e.Args[2],e.Args[3]));Shutdown();return;}
  if(e.Args.Length>=3&&e.Args[0]=="--obs-scan"){await Task.Run(()=>ObsDiagnostic.Scan(e.Args[1],e.Args[2]));Shutdown();return;}
  if(e.Args.Length>=3&&e.Args[0]=="--obs-diagnostic"){await Task.Run(()=>ObsDiagnostic.Run(e.Args[1],e.Args[2]));Shutdown();return;}
  if(e.Args.Length>=2&&e.Args[0]=="--capture-diagnostic"){await LiveAudioDiagnostic.Capture(e.Args[1]);Shutdown();return;}
  if(e.Args.Length>=2&&e.Args[0]=="--audio-routing"){File.WriteAllText(e.Args[1],AudioRouting.Report());Shutdown();return;}
  if(e.Args.Length>=3&&e.Args[0]=="--first-sound-loopback"){await FirstSoundValidation.Loopback(e.Args[1],e.Args[2]);Shutdown();return;}
  if(e.Args.Length>=3&&e.Args[0]=="--first-sound-test"){await Task.Run(()=>FirstSoundValidation.Run(e.Args[1],e.Args[2]));Shutdown();return;}
  if(e.Args.Length>=3&&e.Args[0]=="--installation-test"){await InstallationValidation.Run(e.Args[1],e.Args[2]);Shutdown();return;}
  if(e.Args.Length>=3&&e.Args[0]=="--bulk-validate"){await BulkValidation.Run(e.Args[1],e.Args[2]);Shutdown();return;}
  if(e.Args.Contains("--install-seed")){var database=new CatalogStore();database.Load();SeedCatalog.Merge(database);Shutdown();return;}
  if(e.Args.Length>=2&&e.Args[0]=="--bulk-cluster"){await Task.Run(()=>BulkCluster.Run(e.Args[1]));Shutdown();return;}
  if(e.Args.Length>=2&&e.Args[0]=="--bulk-export"){await Task.Run(()=>BulkExport.Run(e.Args[1]));Shutdown();return;}
  if(e.Args.Length>=3&&e.Args[0]=="--recognition-test") { await RecognitionValidation.Run(e.Args[1],e.Args[2],e.Args.Contains("--loopback-test"));Shutdown();return; }
  if(e.Args.Contains("--self-test")) { await SelfTest.Run(e.Args.Contains("--audio-test")); Shutdown(); return; }
  if(e.Args.Contains("--market-readonly")){
   var market=new Market.MarketWindow(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"FieldTrader"));MainWindow=market;market.Show();return;
  }
  var store=new CatalogStore();store.Load();SeedCatalog.Merge(store);session=new(store,Dispatcher);MainWindow=new MainWindow(session);MainWindow.Show();
 } catch(Exception ex) { File.WriteAllText(Path.Combine(AppContext.BaseDirectory,"startup-error.txt"),ex.ToString()); if(e.Args.Length==0) MessageBox.Show(ex.Message,"战地行商 · 启动失败");Shutdown(1); } }
 protected override void OnExit(ExitEventArgs e) {session?.Dispose();base.OnExit(e);}
}








