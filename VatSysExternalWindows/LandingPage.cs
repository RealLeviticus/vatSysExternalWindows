namespace VatSysExternalWindows;

public static class LandingPage
{
    public const string Html = @"<!DOCTYPE html>
<html lang='en'>
<head>
<meta charset='utf-8'>
<meta name='viewport' content='width=device-width,initial-scale=1,user-scalable=no'>
<meta name='apple-mobile-web-app-capable' content='yes'>
<title>vatSys External Windows</title>
<style>
@font-face{font-family:'Terminus';src:url('/font.ttf') format('truetype');font-weight:bold;font-style:normal;}
*{margin:0;padding:0;box-sizing:border-box;}
html,body{height:100%;background:#a0aaaa;font-family:'Terminus',monospace;font-weight:bold;overflow:hidden;-webkit-user-select:none;user-select:none;}
.container{display:flex;flex-direction:column;align-items:center;justify-content:center;height:100%;gap:30px;}
h1{color:#000060;font-size:22px;letter-spacing:1px;text-align:center;}
.buttons{display:flex;gap:24px;flex-wrap:wrap;justify-content:center;}
a.btn{display:flex;align-items:center;justify-content:center;width:200px;height:80px;background:#829292;border:2px solid #5a6a6a;color:#000060;font-family:'Terminus',monospace;font-weight:bold;font-size:18px;text-decoration:none;letter-spacing:1px;transition:background 0.1s;}
a.btn:active{background:#000060;color:#c8ffc8;}
.subtitle{color:#600000;font-size:14px;text-align:center;}
</style>
</head>
<body>
<div class='container'>
  <h1>vatSys External Windows</h1>
  <div class='buttons'>
    <a class='btn' href='/ground'>Ground<br>Window</a>
    <a class='btn' href='/asd'>Air<br>Window</a>
  </div>
  <div class='subtitle'>Select a window type to display</div>
</div>
</body>
</html>";
}
