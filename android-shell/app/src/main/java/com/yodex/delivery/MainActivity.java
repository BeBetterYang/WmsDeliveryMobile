package com.yodex.delivery;

import android.Manifest;
import android.annotation.SuppressLint;
import android.app.Activity;
import android.content.ClipData;
import android.content.ActivityNotFoundException;
import android.content.Context;
import android.content.Intent;
import android.content.SharedPreferences;
import android.content.pm.ActivityInfo;
import android.content.pm.PackageManager;
import android.content.pm.ResolveInfo;
import android.graphics.Canvas;
import android.graphics.Color;
import android.graphics.Paint;
import android.graphics.Rect;
import android.hardware.Camera;
import android.net.Uri;
import android.os.Bundle;
import android.os.Handler;
import android.os.Looper;
import android.provider.MediaStore;
import android.view.Gravity;
import android.view.KeyEvent;
import android.view.SurfaceHolder;
import android.view.SurfaceView;
import android.view.View;
import android.view.ViewGroup;
import android.webkit.JavascriptInterface;
import android.webkit.PermissionRequest;
import android.webkit.ValueCallback;
import android.webkit.WebChromeClient;
import android.webkit.WebResourceError;
import android.webkit.WebResourceRequest;
import android.webkit.WebSettings;
import android.webkit.WebView;
import android.webkit.WebViewClient;
import android.widget.Button;
import android.widget.EditText;
import android.widget.FrameLayout;
import android.widget.LinearLayout;
import android.widget.TextView;
import android.widget.Toast;

import androidx.core.content.FileProvider;
import androidx.swiperefreshlayout.widget.SwipeRefreshLayout;

import com.google.zxing.BarcodeFormat;
import com.google.zxing.BinaryBitmap;
import com.google.zxing.DecodeHintType;
import com.google.zxing.LuminanceSource;
import com.google.zxing.MultiFormatReader;
import com.google.zxing.PlanarYUVLuminanceSource;
import com.google.zxing.Result;
import com.google.zxing.common.GlobalHistogramBinarizer;
import com.google.zxing.common.HybridBinarizer;
import com.google.zxing.integration.android.IntentIntegrator;
import com.google.zxing.integration.android.IntentResult;

import java.io.File;
import java.util.Collection;
import java.util.EnumMap;
import java.util.EnumSet;
import java.util.List;
import java.util.Map;

public class MainActivity extends Activity {
    private static final String PREFS = "wms_delivery_shell";
    private static final String KEY_SERVER_URL = "server_url";
    private static final int REQ_WEB_CAMERA = 1001;
    private static final int REQ_SCAN_CAMERA = 1002;
    private static final int REQ_FILE_CHOOSER = 1003;
    private static final int REQ_FILE_CAMERA = 1004;
    private static final int SCAN_MODE_SERVER = 1;
    private static final int SCAN_MODE_WEB = 2;

    private SharedPreferences prefs;
    private WebView webView;
    private SwipeRefreshLayout swipeRefreshLayout;
    private EditText serverInput;
    private PermissionRequest pendingPermissionRequest;
    private ValueCallback<Uri[]> filePathCallback;
    private Uri cameraImageUri;
    private boolean pendingFileCaptureOnly;
    private String lastServerUrl = "";

    private Camera scannerCamera;
    private FrameLayout scannerPanel;
    private SurfaceView scannerSurface;
    private Camera.Size scannerPreviewSize;
    private boolean scannerActive;
    private boolean decodingFrame;
    private int scannerMode = SCAN_MODE_SERVER;
    private long lastAutoFocusAt;
    private long lastBackPressedAt;
    private final MultiFormatReader barcodeReader = new MultiFormatReader();
    private final Handler mainHandler = new Handler(Looper.getMainLooper());

    @Override
    protected void onCreate(Bundle savedInstanceState) {
        super.onCreate(savedInstanceState);
        prefs = getSharedPreferences(PREFS, MODE_PRIVATE);
        setupBarcodeReader();

        String savedUrl = prefs.getString(KEY_SERVER_URL, BuildConfig.DEFAULT_SERVER_URL);
        if (savedUrl == null || savedUrl.trim().isEmpty()) {
            showServerSetup("");
        } else {
            openWeb(normalizeServerUrl(savedUrl));
        }
    }

    private void setupBarcodeReader() {
        Map<DecodeHintType, Object> hints = new EnumMap<>(DecodeHintType.class);
        hints.put(DecodeHintType.TRY_HARDER, Boolean.TRUE);
        hints.put(DecodeHintType.ALSO_INVERTED, Boolean.TRUE);
        Collection<BarcodeFormat> formats = EnumSet.of(
                BarcodeFormat.QR_CODE,
                BarcodeFormat.CODE_128,
                BarcodeFormat.CODE_39,
                BarcodeFormat.CODE_93,
                BarcodeFormat.EAN_13,
                BarcodeFormat.EAN_8,
                BarcodeFormat.UPC_A,
                BarcodeFormat.UPC_E,
                BarcodeFormat.ITF,
                BarcodeFormat.CODABAR
        );
        hints.put(DecodeHintType.POSSIBLE_FORMATS, formats);
        barcodeReader.setHints(hints);
    }

    private void showServerSetup(String initialValue) {
        stopScanner();
        webView = null;

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setGravity(Gravity.CENTER_HORIZONTAL);
        root.setPadding(dp(24), dp(54), dp(24), dp(24));
        root.setBackgroundColor(Color.rgb(245, 247, 250));

        TextView title = new TextView(this);
        title.setText("配送");
        title.setTextSize(30);
        title.setTextColor(Color.rgb(23, 32, 51));
        title.setGravity(Gravity.CENTER);
        title.setTypeface(null, 1);
        root.addView(title, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT));

        TextView tip = new TextView(this);
        tip.setText("请输入服务器地址");
        tip.setTextSize(15);
        tip.setTextColor(Color.rgb(91, 101, 121));
        tip.setGravity(Gravity.CENTER);
        LinearLayout.LayoutParams tipParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.WRAP_CONTENT);
        tipParams.setMargins(0, dp(10), 0, 0);
        root.addView(tip, tipParams);

        serverInput = new EditText(this);
        String value = initialValue == null || initialValue.trim().isEmpty() ? "http://" : initialValue;
        serverInput.setSingleLine(true);
        serverInput.setText(value);
        serverInput.setTextSize(16);
        serverInput.setPadding(dp(14), 0, dp(14), 0);
        LinearLayout.LayoutParams inputParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(48));
        inputParams.setMargins(0, dp(30), 0, 0);
        root.addView(serverInput, inputParams);

        Button enterButton = primaryButton("进入");
        enterButton.setOnClickListener(v -> saveAndOpen(serverInput.getText().toString()));
        LinearLayout.LayoutParams enterParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(48));
        enterParams.setMargins(0, dp(18), 0, 0);
        root.addView(enterButton, enterParams);

        Button scanButton = secondaryButton("扫码填入地址");
        scanButton.setOnClickListener(v -> startServerScanner());
        LinearLayout.LayoutParams scanParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(46));
        scanParams.setMargins(0, dp(12), 0, 0);
        root.addView(scanButton, scanParams);

        String savedUrl = prefs.getString(KEY_SERVER_URL, "");
        if (savedUrl != null && !savedUrl.isEmpty()) {
            Button clearButton = secondaryButton("清除保存地址");
            clearButton.setOnClickListener(v -> {
                prefs.edit().remove(KEY_SERVER_URL).apply();
                serverInput.setText("http://");
                Toast.makeText(this, "已清除", Toast.LENGTH_SHORT).show();
            });
            LinearLayout.LayoutParams clearParams = new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(46));
            clearParams.setMargins(0, dp(12), 0, 0);
            root.addView(clearButton, clearParams);
        }

        setContentView(root);
        serverInput.requestFocus();
    }

    private Button primaryButton(String text) {
        Button button = new Button(this);
        button.setAllCaps(false);
        button.setText(text);
        button.setTextSize(16);
        button.setTextColor(Color.WHITE);
        button.setBackgroundColor(Color.rgb(22, 119, 255));
        return button;
    }

    private Button secondaryButton(String text) {
        Button button = new Button(this);
        button.setAllCaps(false);
        button.setText(text);
        button.setTextSize(16);
        button.setTextColor(Color.rgb(22, 119, 255));
        button.setBackgroundColor(Color.WHITE);
        return button;
    }

    private void saveAndOpen(String value) {
        String url;
        try {
            url = normalizeServerUrl(value);
        } catch (IllegalArgumentException ex) {
            Toast.makeText(this, ex.getMessage(), Toast.LENGTH_LONG).show();
            return;
        }
        prefs.edit().putString(KEY_SERVER_URL, url).apply();
        openWeb(url);
    }

    private String normalizeServerUrl(String raw) {
        String value = raw == null ? "" : raw.trim();
        if (value.isEmpty() || value.equals("http://") || value.equals("https://")) {
            throw new IllegalArgumentException("请输入服务器地址");
        }
        if (!value.startsWith("http://") && !value.startsWith("https://")) {
            value = "http://" + value;
        }
        Uri uri = Uri.parse(value);
        if (uri.getHost() == null || uri.getHost().trim().isEmpty()) {
            throw new IllegalArgumentException("服务器地址格式不正确");
        }
        if (!value.endsWith("/")) value += "/";
        return value;
    }

    @SuppressLint({"SetJavaScriptEnabled", "AddJavascriptInterface"})
    private void openWeb(String url) {
        stopScanner();
        lastServerUrl = url;
        swipeRefreshLayout = new SwipeRefreshLayout(this);
        webView = new WebView(this);
        swipeRefreshLayout.addView(webView, new SwipeRefreshLayout.LayoutParams(
                ViewGroup.LayoutParams.MATCH_PARENT,
                ViewGroup.LayoutParams.MATCH_PARENT
        ));
        swipeRefreshLayout.setColorSchemeColors(Color.rgb(22, 119, 255));
        swipeRefreshLayout.setOnRefreshListener(() -> {
            if (webView != null) {
                webView.reload();
            } else {
                swipeRefreshLayout.setRefreshing(false);
            }
        });
        swipeRefreshLayout.setOnChildScrollUpCallback((parent, child) ->
                webView != null && webView.getScrollY() > 0);
        setContentView(swipeRefreshLayout);

        WebSettings settings = webView.getSettings();
        settings.setJavaScriptEnabled(true);
        settings.setDomStorageEnabled(true);
        settings.setDatabaseEnabled(true);
        settings.setLoadWithOverviewMode(true);
        settings.setUseWideViewPort(true);
        settings.setMixedContentMode(WebSettings.MIXED_CONTENT_ALWAYS_ALLOW);
        settings.setMediaPlaybackRequiresUserGesture(false);

        webView.setWebViewClient(new WebViewClient() {
            @Override
            public boolean shouldOverrideUrlLoading(WebView view, WebResourceRequest request) {
                return handleExternalUrl(request == null ? null : request.getUrl());
            }

            @Override
            public boolean shouldOverrideUrlLoading(WebView view, String url) {
                return handleExternalUrl(url == null ? null : Uri.parse(url));
            }

            @Override
            public void onPageFinished(WebView view, String url) {
                super.onPageFinished(view, url);
                if (swipeRefreshLayout != null) swipeRefreshLayout.setRefreshing(false);
                installNativeScannerHook(view);
            }

            @Override
            public void onReceivedError(WebView view, WebResourceRequest request, WebResourceError error) {
                super.onReceivedError(view, request, error);
                if (request != null && request.isForMainFrame()) {
                    if (swipeRefreshLayout != null) swipeRefreshLayout.setRefreshing(false);
                    Toast.makeText(MainActivity.this, "无法连接服务器，请检查地址", Toast.LENGTH_LONG).show();
                    showServerSetup(lastServerUrl);
                }
            }
        });

        webView.setWebChromeClient(new WebChromeClient() {
            @Override
            public void onPermissionRequest(PermissionRequest request) {
                pendingPermissionRequest = request;
                if (checkSelfPermission(Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED) {
                    request.grant(request.getResources());
                } else {
                    requestPermissions(new String[]{Manifest.permission.CAMERA}, REQ_WEB_CAMERA);
                }
            }

            @Override
            public boolean onShowFileChooser(WebView webView, ValueCallback<Uri[]> callback, FileChooserParams params) {
                if (filePathCallback != null) {
                    filePathCallback.onReceiveValue(null);
                }
                filePathCallback = callback;
                boolean captureOnly = params != null && params.isCaptureEnabled() && acceptsImage(params);
                pendingFileCaptureOnly = captureOnly;
                if (captureOnly && checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
                    requestPermissions(new String[]{Manifest.permission.CAMERA}, REQ_FILE_CAMERA);
                    return true;
                }
                openImageChooser(captureOnly);
                return true;
            }
        });

        webView.addJavascriptInterface(new NativeBridge(), "YodexNative");
        webView.loadUrl(url);
    }

    private void installNativeScannerHook(WebView view) {
        String script =
                "(function(){"
                        + "if(window.__deliveryNativeScannerHookInstalled)return;"
                        + "window.__deliveryNativeScannerHookInstalled=true;"
                        + "window.__deliveryNativeScanFallback=function(code){"
                        + "code=String(code||'').trim();"
                        + "if(!code)return;"
                        + "var input=document.querySelector('.adm-search-bar input,input[type=search],input');"
                        + "if(!input)return;"
                        + "var setter=Object.getOwnPropertyDescriptor(HTMLInputElement.prototype,'value').set;"
                        + "setter.call(input,code);"
                        + "input.dispatchEvent(new Event('input',{bubbles:true}));"
                        + "input.dispatchEvent(new Event('change',{bubbles:true}));"
                        + "};"
                        + "if(!window.__yodexNativeScanResult){"
                        + "window.__yodexNativeScanResult=window.__deliveryNativeScanFallback;"
                        + "}"
                        + "document.addEventListener('click',function(event){"
                        + "var closest=event.target&&event.target.closest;"
                        + "var button=closest&&event.target.closest('.scan-button');"
                        + "if(button&&window.YodexNative&&window.YodexNative.scanCode){"
                        + "event.preventDefault();"
                        + "event.stopPropagation();"
                        + "window.YodexNative.scanCode();"
                        + "}"
                        + "},true);"
                        + "})();";
        view.evaluateJavascript(script, null);
    }

    private boolean handleExternalUrl(Uri uri) {
        if (uri == null) return false;
        String scheme = uri.getScheme();
        if (scheme == null) return false;
        if (scheme.equals("http") || scheme.equals("https")) return false;

        try {
            Intent intent;
            if (scheme.equals("intent")) {
                intent = Intent.parseUri(uri.toString(), Intent.URI_INTENT_SCHEME);
            } else {
                intent = new Intent(Intent.ACTION_VIEW, uri);
            }
            startActivity(intent);
            return true;
        } catch (Exception ex) {
            Toast.makeText(this, "无法打开外部应用", Toast.LENGTH_SHORT).show();
            return true;
        }
    }

    private boolean acceptsImage(WebChromeClient.FileChooserParams params) {
        String[] acceptTypes = params.getAcceptTypes();
        if (acceptTypes == null || acceptTypes.length == 0) return true;
        for (String acceptType : acceptTypes) {
            if (acceptType == null || acceptType.trim().isEmpty()) return true;
            if (acceptType.toLowerCase().contains("image")) return true;
        }
        return false;
    }

    private Intent createCameraIntent() {
        try {
            File imageFile = File.createTempFile("delivery-photo-", ".jpg", getCacheDir());
            cameraImageUri = FileProvider.getUriForFile(this, getPackageName() + ".fileprovider", imageFile);
            Intent cameraIntent = new Intent(MediaStore.ACTION_IMAGE_CAPTURE);
            cameraIntent.putExtra(MediaStore.EXTRA_OUTPUT, cameraImageUri);
            cameraIntent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
            cameraIntent.setClipData(ClipData.newRawUri("delivery-photo", cameraImageUri));
            List<ResolveInfo> cameraApps = getPackageManager().queryIntentActivities(cameraIntent, PackageManager.MATCH_DEFAULT_ONLY);
            for (ResolveInfo resolveInfo : cameraApps) {
                grantUriPermission(resolveInfo.activityInfo.packageName, cameraImageUri, Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
            }
            return cameraIntent;
        } catch (Exception ignored) {
            cameraImageUri = null;
            return null;
        }
    }

    private void openImageChooser(boolean captureOnly) {
        Intent cameraIntent = createCameraIntent();
        if (captureOnly) {
            if (cameraIntent == null) {
                finishFileChooserWithError("Cannot create camera file");
                return;
            }
            try {
                startActivityForResult(cameraIntent, REQ_FILE_CHOOSER);
                return;
            } catch (ActivityNotFoundException ex) {
                finishFileChooserWithError("No camera app is available");
                return;
            }
        }

        Intent galleryIntent = new Intent(Intent.ACTION_GET_CONTENT);
        galleryIntent.addCategory(Intent.CATEGORY_OPENABLE);
        galleryIntent.setType("image/*");

        Intent chooser = Intent.createChooser(galleryIntent, "Choose or take photo");
        if (cameraIntent != null) {
            chooser.putExtra(Intent.EXTRA_INITIAL_INTENTS, new Intent[]{cameraIntent});
        }

        try {
            startActivityForResult(chooser, REQ_FILE_CHOOSER);
        } catch (ActivityNotFoundException ex) {
            finishFileChooserWithError("No image picker is available");
        }
    }

    private void finishFileChooserWithError(String message) {
        if (filePathCallback != null) {
            filePathCallback.onReceiveValue(null);
            filePathCallback = null;
        }
        cameraImageUri = null;
        pendingFileCaptureOnly = false;
        Toast.makeText(this, message, Toast.LENGTH_LONG).show();
    }

    private void openImageChooser() {
        Intent cameraIntent = null;
        try {
            File imageFile = File.createTempFile("delivery-photo-", ".jpg", getCacheDir());
            cameraImageUri = FileProvider.getUriForFile(this, getPackageName() + ".fileprovider", imageFile);
            cameraIntent = new Intent(MediaStore.ACTION_IMAGE_CAPTURE);
            cameraIntent.putExtra(MediaStore.EXTRA_OUTPUT, cameraImageUri);
            cameraIntent.addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION | Intent.FLAG_GRANT_WRITE_URI_PERMISSION);
        } catch (Exception ignored) {
            cameraImageUri = null;
        }

        Intent galleryIntent = new Intent(Intent.ACTION_GET_CONTENT);
        galleryIntent.addCategory(Intent.CATEGORY_OPENABLE);
        galleryIntent.setType("image/*");

        Intent chooser = Intent.createChooser(galleryIntent, "选择或拍摄照片");
        if (cameraIntent != null && cameraIntent.resolveActivity(getPackageManager()) != null) {
            chooser.putExtra(Intent.EXTRA_INITIAL_INTENTS, new Intent[]{cameraIntent});
        }

        try {
            startActivityForResult(chooser, REQ_FILE_CHOOSER);
        } catch (ActivityNotFoundException ex) {
            if (filePathCallback != null) {
                filePathCallback.onReceiveValue(null);
                filePathCallback = null;
            }
            Toast.makeText(this, "没有可用的图片选择器", Toast.LENGTH_LONG).show();
        }
    }

    private void startServerScanner() {
        scannerMode = SCAN_MODE_SERVER;
        startScannerWithPermission();
    }

    private void startWebScanner() {
        scannerMode = SCAN_MODE_WEB;
        startScannerWithPermission();
    }

    private void startEmbeddedScanner() {
        IntentIntegrator integrator = new IntentIntegrator(this);
        integrator.setDesiredBarcodeFormats(IntentIntegrator.ALL_CODE_TYPES);
        integrator.setPrompt(scannerMode == SCAN_MODE_SERVER ? "请扫描服务器地址二维码" : "请扫描二维码或一维码");
        integrator.setCameraId(0);
        integrator.setBeepEnabled(false);
        integrator.setBarcodeImageEnabled(false);
        integrator.setOrientationLocked(false);
        integrator.initiateScan();
    }

    private void startScannerWithPermission() {
        if (checkSelfPermission(Manifest.permission.CAMERA) != PackageManager.PERMISSION_GRANTED) {
            requestPermissions(new String[]{Manifest.permission.CAMERA}, REQ_SCAN_CAMERA);
            return;
        }
        showScannerView();
    }

    private void showScannerView() {
        stopScanner();
        scannerActive = true;

        LinearLayout root = new LinearLayout(this);
        root.setOrientation(LinearLayout.VERTICAL);
        root.setBackgroundColor(Color.WHITE);

        FrameLayout nav = new FrameLayout(this);
        nav.setBackgroundColor(Color.rgb(22, 119, 255));

        TextView back = new TextView(this);
        back.setText("‹");
        back.setTextColor(Color.WHITE);
        back.setTextSize(34);
        back.setGravity(Gravity.CENTER);
        back.setOnClickListener(v -> cancelScanner());
        nav.addView(back, new FrameLayout.LayoutParams(dp(56), ViewGroup.LayoutParams.MATCH_PARENT, Gravity.LEFT | Gravity.CENTER_VERTICAL));

        TextView title = new TextView(this);
        title.setText(scannerMode == SCAN_MODE_SERVER ? "扫码连接" : "扫码");
        title.setTextColor(Color.WHITE);
        title.setTextSize(18);
        title.setGravity(Gravity.CENTER);
        title.setTypeface(null, 1);
        nav.addView(title, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        root.addView(nav, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(45)));

        scannerPanel = new FrameLayout(this);
        scannerPanel.setBackgroundColor(Color.BLACK);
        scannerSurface = new SurfaceView(this);
        scannerSurface.setOnClickListener(v -> triggerAutoFocus());
        scannerPanel.addView(scannerSurface, new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT, Gravity.CENTER));
        scannerPanel.addView(new ScannerOverlayView(this), new FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT));
        root.addView(scannerPanel, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(280)));

        TextView tip = new TextView(this);
        tip.setText(scannerMode == SCAN_MODE_SERVER ? "请扫描服务器地址二维码" : "请将二维码或一维码放入取景框");
        tip.setTextSize(15);
        tip.setTextColor(Color.rgb(91, 101, 121));
        tip.setGravity(Gravity.CENTER);
        root.addView(tip, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, dp(56)));

        View blank = new View(this);
        root.addView(blank, new LinearLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, 0, 1));

        setContentView(root);
        scannerSurface.getHolder().addCallback(new SurfaceHolder.Callback() {
            @Override
            public void surfaceCreated(SurfaceHolder holder) {
                openScannerCamera(holder);
            }

            @Override
            public void surfaceChanged(SurfaceHolder holder, int format, int width, int height) {
                adjustScannerSurfaceLayout();
            }

            @Override
            public void surfaceDestroyed(SurfaceHolder holder) {
                stopScanner();
            }
        });
    }

    private void cancelScanner() {
        stopScanner();
        if (scannerMode == SCAN_MODE_WEB && webView != null) {
            restoreWebContent();
            return;
        }
        showServerSetup(serverInput == null ? lastServerUrl : serverInput.getText().toString());
    }

    private void openScannerCamera(SurfaceHolder holder) {
        try {
            scannerCamera = Camera.open();
            Camera.Parameters params = scannerCamera.getParameters();
            Camera.Size previewSize = choosePreviewSize(params.getSupportedPreviewSizes());
            if (previewSize != null) {
                params.setPreviewSize(previewSize.width, previewSize.height);
                scannerPreviewSize = previewSize;
            }
            List<String> focusModes = params.getSupportedFocusModes();
            if (focusModes != null) {
                if (focusModes.contains(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE)) {
                    params.setFocusMode(Camera.Parameters.FOCUS_MODE_CONTINUOUS_PICTURE);
                } else if (focusModes.contains(Camera.Parameters.FOCUS_MODE_AUTO)) {
                    params.setFocusMode(Camera.Parameters.FOCUS_MODE_AUTO);
                }
            }
            scannerCamera.setParameters(params);
            scannerCamera.setDisplayOrientation(90);
            scannerCamera.setPreviewDisplay(holder);
            scannerCamera.setPreviewCallback(this::decodePreviewFrame);
            scannerCamera.startPreview();
            adjustScannerSurfaceLayout();
            triggerAutoFocus();
        } catch (Exception ex) {
            Toast.makeText(this, "无法打开摄像头：" + ex.getMessage(), Toast.LENGTH_LONG).show();
            cancelScanner();
        }
    }

    private Camera.Size choosePreviewSize(List<Camera.Size> sizes) {
        if (sizes == null || sizes.isEmpty()) return null;
        Camera.Size best = null;
        for (Camera.Size size : sizes) {
            if (size.width < 1280 || size.height < 720) continue;
            int area = size.width * size.height;
            int bestArea = best == null ? 0 : best.width * best.height;
            if (area <= 1920 * 1080 && area > bestArea) best = size;
        }
        if (best != null) return best;
        for (Camera.Size size : sizes) {
            if (best == null || size.width * size.height > best.width * best.height) best = size;
        }
        return best;
    }

    private void adjustScannerSurfaceLayout() {
        if (scannerPanel == null || scannerSurface == null || scannerPreviewSize == null) return;
        scannerPanel.post(() -> {
            int panelWidth = scannerPanel.getWidth();
            int panelHeight = scannerPanel.getHeight();
            if (panelWidth <= 0 || panelHeight <= 0) return;
            double previewRatio = (double) scannerPreviewSize.height / (double) scannerPreviewSize.width;
            double panelRatio = (double) panelWidth / (double) panelHeight;
            int surfaceWidth;
            int surfaceHeight;
            if (panelRatio > previewRatio) {
                surfaceWidth = panelWidth;
                surfaceHeight = (int) Math.ceil(panelWidth / previewRatio);
            } else {
                surfaceHeight = panelHeight;
                surfaceWidth = (int) Math.ceil(panelHeight * previewRatio);
            }
            scannerSurface.setLayoutParams(new FrameLayout.LayoutParams(surfaceWidth, surfaceHeight, Gravity.CENTER));
        });
    }

    private void triggerAutoFocus() {
        if (scannerCamera == null) return;
        long now = System.currentTimeMillis();
        if (now - lastAutoFocusAt < 1200) return;
        lastAutoFocusAt = now;
        try {
            scannerCamera.autoFocus((success, camera) -> {
            });
        } catch (Exception ignored) {
        }
    }

    private void decodePreviewFrame(byte[] data, Camera camera) {
        if (!scannerActive || decodingFrame) return;
        decodingFrame = true;
        try {
            Camera.Size size = camera.getParameters().getPreviewSize();
            Result result = decodeFull(data, size);
            if (result == null) result = decodeCrop(data, size, buildHorizontalCrop(size.width, size.height));
            if (result == null) result = decodeCrop(data, size, buildVerticalCrop(size.width, size.height));
            if (result == null) result = decodeCrop(data, size, buildCenterCrop(size.width, size.height));
            if (result != null) handleScanResult(result.getText());
        } catch (Exception ignored) {
            barcodeReader.reset();
        } finally {
            decodingFrame = false;
        }
    }

    private Rect buildHorizontalCrop(int width, int height) {
        int cropWidth = Math.max(width * 9 / 10, width - 32);
        int cropHeight = Math.max(height / 4, Math.min(height, 300));
        int left = Math.max(0, (width - cropWidth) / 2);
        int top = Math.max(0, (height - cropHeight) / 2);
        return new Rect(left, top, left + cropWidth, top + cropHeight);
    }

    private Rect buildVerticalCrop(int width, int height) {
        int cropWidth = Math.max(width / 4, Math.min(width, 340));
        int cropHeight = Math.max(height * 9 / 10, height - 32);
        int left = Math.max(0, (width - cropWidth) / 2);
        int top = Math.max(0, (height - cropHeight) / 2);
        return new Rect(left, top, left + cropWidth, top + cropHeight);
    }

    private Rect buildCenterCrop(int width, int height) {
        int cropWidth = Math.max(width / 2, Math.min(width, 760));
        int cropHeight = Math.max(height / 2, Math.min(height, 760));
        int left = Math.max(0, (width - cropWidth) / 2);
        int top = Math.max(0, (height - cropHeight) / 2);
        return new Rect(left, top, left + cropWidth, top + cropHeight);
    }

    private Result decodeFull(byte[] data, Camera.Size size) {
        PlanarYUVLuminanceSource source = new PlanarYUVLuminanceSource(
                data, size.width, size.height, 0, 0, size.width, size.height, false);
        return decodeSource(source);
    }

    private Result decodeCrop(byte[] data, Camera.Size size, Rect crop) {
        PlanarYUVLuminanceSource source = new PlanarYUVLuminanceSource(
                data, size.width, size.height, crop.left, crop.top, crop.width(), crop.height(), false);
        return decodeSource(source);
    }

    private Result decodeSource(LuminanceSource source) {
        Result result = tryDecode(source);
        if (result != null) return result;
        if (source.isRotateSupported()) {
            LuminanceSource rotated = source.rotateCounterClockwise();
            result = tryDecode(rotated);
            if (result != null) return result;
            rotated = rotated.rotateCounterClockwise();
            result = tryDecode(rotated);
            if (result != null) return result;
            rotated = rotated.rotateCounterClockwise();
            return tryDecode(rotated);
        }
        return null;
    }

    private Result tryDecode(LuminanceSource source) {
        Result result = tryDecodeBitmap(source);
        if (result != null) return result;
        try {
            return tryDecodeBitmap(source.invert());
        } catch (Exception ignored) {
            return null;
        }
    }

    private Result tryDecodeBitmap(LuminanceSource source) {
        try {
            return barcodeReader.decodeWithState(new BinaryBitmap(new HybridBinarizer(source)));
        } catch (Exception ignored) {
            barcodeReader.reset();
        }
        try {
            return barcodeReader.decodeWithState(new BinaryBitmap(new GlobalHistogramBinarizer(source)));
        } catch (Exception ignored) {
            barcodeReader.reset();
            return null;
        }
    }

    private void handleScanResult(String text) {
        if (text == null || text.trim().isEmpty()) return;
        String value = text.trim();
        runOnUiThread(() -> {
            stopScanner();
            if (scannerMode == SCAN_MODE_WEB && webView != null) {
                restoreWebContent();
                dispatchWebScanResult(value);
                return;
            }
            try {
                String url = normalizeServerUrl(value);
                prefs.edit().putString(KEY_SERVER_URL, url).apply();
                Toast.makeText(this, "服务器地址已保存", Toast.LENGTH_SHORT).show();
                openWeb(url);
            } catch (IllegalArgumentException ex) {
                Toast.makeText(this, "二维码不是有效服务器地址", Toast.LENGTH_LONG).show();
                showServerSetup(value);
            }
        });
    }

    private void restoreWebContent() {
        if (swipeRefreshLayout != null) {
            setContentView(swipeRefreshLayout);
        } else if (webView != null) {
            setContentView(webView);
        }
    }

    private void stopScanner() {
        scannerActive = false;
        decodingFrame = false;
        if (scannerCamera != null) {
            try {
                scannerCamera.setPreviewCallback(null);
                scannerCamera.stopPreview();
                scannerCamera.release();
            } catch (Exception ignored) {
            }
            scannerCamera = null;
        }
    }

    private void dispatchWebScanResult(String value) {
        if (webView == null) return;
        String escaped = value
                .replace("\\", "\\\\")
                .replace("'", "\\'")
                .replace("\n", "\\n")
                .replace("\r", "");
        webView.evaluateJavascript("window.__yodexNativeScanResult && window.__yodexNativeScanResult('" + escaped + "')", null);
    }

    @Override
    protected void onActivityResult(int requestCode, int resultCode, Intent data) {
        super.onActivityResult(requestCode, resultCode, data);
        IntentResult scanResult = IntentIntegrator.parseActivityResult(requestCode, resultCode, data);
        if (scanResult != null) {
            if (scanResult.getContents() != null) {
                handleScanResult(scanResult.getContents());
            }
            return;
        }
        if (requestCode != REQ_FILE_CHOOSER || filePathCallback == null) return;
        Uri[] results = null;
        if (resultCode == RESULT_OK) {
            if (data != null && data.getData() != null) {
                results = new Uri[]{data.getData()};
            } else if (cameraImageUri != null) {
                results = new Uri[]{cameraImageUri};
            }
        }
        filePathCallback.onReceiveValue(results);
        filePathCallback = null;
        cameraImageUri = null;
        pendingFileCaptureOnly = false;
    }

    @Override
    public void onRequestPermissionsResult(int requestCode, String[] permissions, int[] grantResults) {
        super.onRequestPermissionsResult(requestCode, permissions, grantResults);
        if (requestCode == REQ_WEB_CAMERA && pendingPermissionRequest != null) {
            if (grantResults.length > 0 && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
                pendingPermissionRequest.grant(pendingPermissionRequest.getResources());
            } else {
                pendingPermissionRequest.deny();
            }
            pendingPermissionRequest = null;
            return;
        }
        if (requestCode == REQ_SCAN_CAMERA) {
            if (grantResults.length > 0 && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
                showScannerView();
            } else {
                Toast.makeText(this, "没有摄像头权限，无法扫码", Toast.LENGTH_LONG).show();
            }
            return;
        }
        if (requestCode == REQ_FILE_CAMERA) {
            if (grantResults.length > 0 && grantResults[0] == PackageManager.PERMISSION_GRANTED) {
                openImageChooser(pendingFileCaptureOnly);
            } else {
                finishFileChooserWithError("没有摄像头权限，无法拍照");
            }
        }
    }

    @Override
    public boolean onKeyDown(int keyCode, KeyEvent event) {
        if (keyCode == KeyEvent.KEYCODE_BACK) {
            if (scannerActive) {
                cancelScanner();
                return true;
            }
            if (webView != null) {
                handleBackFromWeb();
                return true;
            }
        }
        return super.onKeyDown(keyCode, event);
    }

    private void handleBackFromWeb() {
        webView.evaluateJavascript(
                "(function(){try{return !!(window.__wmsAndroidBack&&window.__wmsAndroidBack());}catch(e){return false;}})();",
                handled -> {
                    if ("true".equals(handled)) return;
                    if (webView != null && webView.canGoBack()) {
                        webView.goBack();
                        return;
                    }
                    exitOrToast();
                });
    }

    private void exitOrToast() {
        long now = System.currentTimeMillis();
        if (now - lastBackPressedAt < 1800) {
            finish();
            return;
        }
        lastBackPressedAt = now;
        Toast.makeText(this, "再按一次退出", Toast.LENGTH_SHORT).show();
    }

    @Override
    protected void onPause() {
        super.onPause();
        stopScanner();
    }

    @Override
    protected void onDestroy() {
        if (filePathCallback != null) {
            filePathCallback.onReceiveValue(null);
            filePathCallback = null;
        }
        super.onDestroy();
    }

    private int dp(int value) {
        return (int) (value * getResources().getDisplayMetrics().density + 0.5f);
    }

    private class NativeBridge {
        @JavascriptInterface
        public void scanCode() {
            runOnUiThread(() -> startWebScanner());
        }

        @JavascriptInterface
        public void setSignatureFullscreen(boolean fullscreen) {
            runOnUiThread(() -> setRequestedOrientation(
                    fullscreen
                            ? ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE
                            : ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED));
        }
    }

    private class ScannerOverlayView extends View {
        private final Paint maskPaint = new Paint();
        private final Paint framePaint = new Paint();

        ScannerOverlayView(Context context) {
            super(context);
            maskPaint.setColor(Color.argb(105, 0, 0, 0));
            framePaint.setColor(Color.rgb(22, 119, 255));
            framePaint.setStyle(Paint.Style.STROKE);
            framePaint.setStrokeWidth(dp(3));
        }

        @Override
        protected void onDraw(Canvas canvas) {
            super.onDraw(canvas);
            int width = getWidth();
            int height = getHeight();
            int frameWidth = Math.min(width - dp(48), dp(320));
            int frameHeight = dp(172);
            int left = (width - frameWidth) / 2;
            int top = Math.max(dp(40), (height - frameHeight) / 2);
            int right = left + frameWidth;
            int bottom = top + frameHeight;

            canvas.drawRect(0, 0, width, top, maskPaint);
            canvas.drawRect(0, bottom, width, height, maskPaint);
            canvas.drawRect(0, top, left, bottom, maskPaint);
            canvas.drawRect(right, top, width, bottom, maskPaint);
            canvas.drawRect(left, top, right, bottom, framePaint);
        }
    }
}
