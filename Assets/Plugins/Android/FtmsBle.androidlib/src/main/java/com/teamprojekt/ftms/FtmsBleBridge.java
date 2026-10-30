package com.teamprojekt.ftms;

import android.annotation.SuppressLint;
import android.bluetooth.BluetoothAdapter;
import android.bluetooth.BluetoothDevice;
import android.bluetooth.BluetoothGatt;
import android.bluetooth.BluetoothGattCallback;
import android.bluetooth.BluetoothGattCharacteristic;
import android.bluetooth.BluetoothGattDescriptor;
import android.bluetooth.BluetoothGattService;
import android.bluetooth.BluetoothManager;
import android.bluetooth.BluetoothProfile;
import android.bluetooth.BluetoothStatusCodes;
import android.bluetooth.le.BluetoothLeScanner;
import android.bluetooth.le.ScanCallback;
import android.bluetooth.le.ScanRecord;
import android.bluetooth.le.ScanResult;
import android.bluetooth.le.ScanSettings;
import android.content.Context;
import android.content.pm.PackageManager;
import android.os.Build;
import android.os.Handler;
import android.os.HandlerThread;
import android.os.Looper;
import android.os.ParcelUuid;
import android.util.Base64;
import android.util.Log;

import org.json.JSONArray;
import org.json.JSONException;
import org.json.JSONObject;

import java.lang.reflect.Method;
import java.util.ArrayDeque;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.HashMap;
import java.util.HashSet;
import java.util.List;
import java.util.Locale;
import java.util.Map;
import java.util.Set;
import java.util.UUID;
import java.util.concurrent.CountDownLatch;
import java.util.concurrent.TimeUnit;

/**
 * BLE transport for Unity. Two GATT clients (bike and heart rate), one queued
 * operation per client. FTMS bytes are decoded in C#.
 */
public final class FtmsBleBridge {
    private static final String TAG = "FtmsBle";
    private static final String RECEIVER = "FtmsBleReceiver";
    private static final String METHOD = "OnBleEvent";
    private static final UUID CCCD = UUID.fromString("00002902-0000-1000-8000-00805f9b34fb");
    private static final long OP_TIMEOUT_MS = 8000L;
    private static final long OP_GAP_MS = 150L;

    private static volatile FtmsBleBridge instance;

    private final HandlerThread thread;
    private final Handler handler;
    private final Handler main;
    private final Map<String, Session> sessions = new HashMap<String, Session>();
    private final Map<String, Long> scanSeen = new HashMap<String, Long>();
    private final Set<String> scanNamed = new HashSet<String>();

    private BluetoothLeScanner scanner;
    private ScanCallback scanCallback;
    private int scanGeneration;
    private boolean scanning;
    private boolean scanCompleteSent;

    public static void startScan(int seconds) {
        instance().handler.post(() -> instance().startScanOnThread(seconds));
    }

    public static void stopScan() {
        instance().handler.post(() -> instance().stopScanOnThread());
    }

    public static void connect(String role, String address) {
        final String roleCopy = role;
        final String addressCopy = address;
        instance().handler.post(() -> instance().connectOnThread(roleCopy, addressCopy));
    }

    public static void disconnect(String role) {
        final String roleCopy = role;
        instance().handler.post(() -> instance().disconnectOnThread(roleCopy, "closed"));
    }

    public static void read(String role, String uuid, int requestId) {
        final String roleCopy = role;
        final String uuidCopy = uuid;
        instance().handler.post(() -> instance().enqueue(
                roleCopy, Op.read(uuidCopy, requestId)));
    }

    public static void subscribe(String role, String uuid, int requestId) {
        final String roleCopy = role;
        final String uuidCopy = uuid;
        instance().handler.post(() -> instance().enqueue(
                roleCopy, Op.subscribe(uuidCopy, requestId)));
    }

    public static void write(String role, String uuid, byte[] data, boolean withResponse, int requestId) {
        final byte[] copy = data == null ? new byte[0] : Arrays.copyOf(data, data.length);
        final String roleCopy = role;
        final String uuidCopy = uuid;
        instance().handler.post(() -> instance().enqueue(
                roleCopy, Op.write(uuidCopy, copy, withResponse, requestId)));
    }

    public static void setStopOnQuit(String role, String uuid, byte[] payload) {
        final byte[] copy = payload == null ? null : Arrays.copyOf(payload, payload.length);
        final String roleCopy = role;
        final String uuidCopy = uuid;
        instance().handler.post(() -> instance().applyStop(roleCopy, uuidCopy, copy));
    }

    /** Blocks the caller until a pending stop command finishes or times out, then closes both links. */
    public static void shutdown() {
        FtmsBleBridge bridge = instance();
        CountDownLatch stopped = new CountDownLatch(1);
        bridge.handler.post(() -> bridge.beginStop(stopped));
        await(stopped, 3);
        CountDownLatch closed = new CountDownLatch(1);
        bridge.handler.post(() -> {
            bridge.stopScanOnThread();
            bridge.closeAll();
            closed.countDown();
        });
        await(closed, 1);
    }

    private static void await(CountDownLatch latch, int seconds) {
        try {
            latch.await(seconds, TimeUnit.SECONDS);
        } catch (InterruptedException ignored) {
            Thread.currentThread().interrupt();
        }
    }

    private static FtmsBleBridge instance() {
        if (instance == null) {
            synchronized (FtmsBleBridge.class) {
                if (instance == null) {
                    instance = new FtmsBleBridge();
                }
            }
        }
        return instance;
    }

    private FtmsBleBridge() {
        thread = new HandlerThread("FtmsBle");
        thread.start();
        handler = new Handler(thread.getLooper());
        main = new Handler(Looper.getMainLooper());
    }

    @SuppressLint("MissingPermission")
    private void startScanOnThread(int seconds) {
        scanGeneration++;
        final int generation = scanGeneration;
        stopScanQuiet();
        scanSeen.clear();
        scanNamed.clear();
        scanCompleteSent = false;
        Context context = activity();
        if (context == null) {
            emitError("", "Unity activity is not available");
            finishScan();
            return;
        }
        if (context.checkSelfPermission(android.Manifest.permission.BLUETOOTH_SCAN)
                != PackageManager.PERMISSION_GRANTED) {
            emitError("", "BLUETOOTH_SCAN is not granted");
            finishScan();
            return;
        }
        BluetoothAdapter adapter = adapter(context);
        if (adapter == null || !adapter.isEnabled()) {
            emitError("", "Bluetooth is off");
            finishScan();
            return;
        }
        scanner = adapter.getBluetoothLeScanner();
        if (scanner == null) {
            emitError("", "Bluetooth LE scanner is not available");
            finishScan();
            return;
        }
        if (scanCallback == null) {
            scanCallback = new ScanCallback() {
                @Override
                public void onScanResult(int callbackType, ScanResult result) {
                    ScanResult copy = result;
                    handler.post(() -> handleScan(copy));
                }

                @Override
                public void onScanFailed(int errorCode) {
                    handler.post(() -> {
                        emitError("", "Bluetooth scan failed (" + errorCode + ")");
                        finishScan();
                    });
                }
            };
        }
        try {
            ScanSettings settings = new ScanSettings.Builder()
                    .setScanMode(ScanSettings.SCAN_MODE_LOW_LATENCY)
                    .build();
            scanner.startScan(null, settings, scanCallback);
            scanning = true;
            Log.i(TAG, "scan started");
            if (seconds > 0) {
                handler.postDelayed(() -> {
                    if (generation == scanGeneration) {
                        finishScan();
                    }
                }, seconds * 1000L);
            }
        } catch (SecurityException ex) {
            emitError("", "Bluetooth scan permission denied");
            finishScan();
        } catch (RuntimeException ex) {
            emitError("", ex.getMessage() == null ? "scan failed" : ex.getMessage());
            finishScan();
        }
    }

    private void stopScanOnThread() {
        scanGeneration++;
        finishScan();
    }

    @SuppressLint("MissingPermission")
    private void stopScanQuiet() {
        scanning = false;
        if (scanner != null && scanCallback != null) {
            try {
                scanner.stopScan(scanCallback);
            } catch (RuntimeException ignored) {
            }
        }
    }

    @SuppressLint("MissingPermission")
    private void finishScan() {
        boolean was = scanning;
        stopScanQuiet();
        if (!was && scanCompleteSent) {
            return;
        }
        if (scanCompleteSent) {
            return;
        }
        scanCompleteSent = true;
        send("scanComplete", new JSONObject());
    }

    @SuppressLint("MissingPermission")
    private void handleScan(ScanResult result) {
        if (!scanning || result == null || result.getDevice() == null) {
            return;
        }
        String address = result.getDevice().getAddress();
        if (address == null) {
            return;
        }
        long now = android.os.SystemClock.elapsedRealtime();
        Long previous = scanSeen.get(address);
        ScanRecord record = result.getScanRecord();
        String name = record == null ? null : record.getDeviceName();
        if (name == null || name.length() == 0) {
            try {
                name = result.getDevice().getName();
            } catch (SecurityException ignored) {
                name = "";
            }
        }
        if (name == null) {
            name = "";
        }
        boolean hadName = scanNamed.contains(address);
        boolean nameUpgrade = !hadName && name.length() > 0;
        if (previous != null && now - previous < 750L && !nameUpgrade) {
            return;
        }
        scanSeen.put(address, now);
        if (name.length() > 0) {
            scanNamed.add(address);
        }
        try {
            JSONObject json = new JSONObject();
            json.put("name", name);
            json.put("address", address);
            json.put("rssi", result.getRssi());
            JSONArray uuids = new JSONArray();
            String serviceData = "";
            if (record != null) {
                List<ParcelUuid> advertised = record.getServiceUuids();
                if (advertised != null) {
                    for (ParcelUuid item : advertised) {
                        uuids.put(shortUuid(item.getUuid()));
                    }
                }
                Map<ParcelUuid, byte[]> data = record.getServiceData();
                if (data != null) {
                    for (Map.Entry<ParcelUuid, byte[]> entry : data.entrySet()) {
                        String shortId = shortUuid(entry.getKey().getUuid());
                        boolean listed = false;
                        for (int i = 0; i < uuids.length(); i++) {
                            if (shortId.equals(uuids.getString(i))) {
                                listed = true;
                                break;
                            }
                        }
                        if (!listed) {
                            uuids.put(shortId);
                        }
                        if ("1826".equals(shortId) && entry.getValue() != null) {
                            serviceData = Base64.encodeToString(entry.getValue(), Base64.NO_WRAP);
                        }
                    }
                }
            }
            json.put("uuids", uuids);
            json.put("serviceData", serviceData);
            send("scan", json);
        } catch (JSONException ex) {
            Log.e(TAG, "scan json", ex);
        }
    }

    @SuppressLint("MissingPermission")
    private void connectOnThread(String role, String address) {
        if (role == null || address == null || address.length() == 0) {
            emitError(role == null ? "" : role, "Missing BLE address");
            return;
        }
        Session existing = sessions.get(role);
        if (existing != null && address.equals(existing.address) && existing.gatt != null && !existing.closed) {
            return;
        }
        if (existing != null) {
            existing.closed = true;
            closeGatt(existing);
            sessions.remove(role);
        }
        Context context = activity();
        if (context == null) {
            emitError(role, "Unity activity is not available");
            sendConnection(role, "disconnected", address, "no activity");
            return;
        }
        if (context.checkSelfPermission(android.Manifest.permission.BLUETOOTH_CONNECT)
                != PackageManager.PERMISSION_GRANTED) {
            emitError(role, "BLUETOOTH_CONNECT is not granted");
            sendConnection(role, "disconnected", address, "permission denied");
            return;
        }
        BluetoothAdapter adapter = adapter(context);
        if (adapter == null || !adapter.isEnabled()) {
            emitError(role, "Bluetooth is off");
            sendConnection(role, "disconnected", address, "bluetooth off");
            return;
        }
        BluetoothDevice device;
        try {
            device = adapter.getRemoteDevice(address);
        } catch (IllegalArgumentException ex) {
            emitError(role, "Bad BLE address");
            sendConnection(role, "disconnected", address, "bad address");
            return;
        }
        Session session = new Session(role, address);
        session.callback = new RoleCallback(session);
        sessions.put(role, session);
        sendConnection(role, "connecting", address, "");
        try {
            session.gatt = device.connectGatt(context, false, session.callback, BluetoothDevice.TRANSPORT_LE);
            Log.i(TAG, "connect " + role + " " + address);
        } catch (SecurityException ex) {
            session.closed = true;
            sessions.remove(role);
            emitError(role, "Bluetooth connect permission denied");
            sendConnection(role, "disconnected", address, "permission denied");
        } catch (RuntimeException ex) {
            session.closed = true;
            sessions.remove(role);
            String message = ex.getMessage() == null ? "connect failed" : ex.getMessage();
            emitError(role, message);
            sendConnection(role, "disconnected", address, message);
        }
    }

    private void disconnectOnThread(String role, String detail) {
        Session session = sessions.get(role);
        if (session == null) {
            return;
        }
        drop(session, detail);
    }

    private void applyStop(String role, String uuid, byte[] payload) {
        Session session = sessions.get(role);
        if (session == null) {
            return;
        }
        if (payload == null || payload.length == 0) {
            session.stopOnQuit = false;
            session.stopPayload = null;
            return;
        }
        session.stopOnQuit = true;
        session.stopUuid = uuid;
        session.stopPayload = payload;
    }

    private void beginStop(CountDownLatch latch) {
        Session session = sessions.get("bike");
        if (session == null || !session.stopOnQuit || session.gatt == null
                || session.stopPayload == null || session.closed) {
            latch.countDown();
            return;
        }
        session.stopLatch = latch;
        session.queue.addFirst(Op.write(session.stopUuid, session.stopPayload, true, -100));
        pump(session);
        handler.postDelayed(() -> {
            CountDownLatch pending = session.stopLatch;
            if (pending != null) {
                session.stopLatch = null;
                pending.countDown();
            }
        }, 2500L);
    }

    private void closeAll() {
        for (Session session : new ArrayList<Session>(sessions.values())) {
            drop(session, "closed");
        }
    }

    private void enqueue(String role, Op op) {
        Session session = sessions.get(role);
        if (session == null || session.closed || session.gatt == null) {
            emitOp(op, role, op.kind, -3, null);
            return;
        }
        session.queue.addLast(op);
        pump(session);
    }

    private void pump(Session session) {
        if (session.closed || session.busy || !session.ready || session.gatt == null) {
            return;
        }
        Op op = session.queue.pollFirst();
        if (op == null) {
            return;
        }
        session.busy = true;
        session.current = op;
        session.opToken++;
        final int token = session.opToken;
        boolean started;
        try {
            started = dispatch(session, op);
        } catch (RuntimeException ex) {
            emitError(session.role, ex.getMessage() == null ? "BLE operation failed" : ex.getMessage());
            started = false;
        }
        if (!started) {
            complete(session, token, op, -2, null);
            return;
        }
        handler.postDelayed(() -> {
            if (session.opToken == token && session.current == op) {
                complete(session, token, op, -1, null);
            }
        }, OP_TIMEOUT_MS);
    }

    @SuppressLint("MissingPermission")
    private boolean dispatch(Session session, Op op) {
        BluetoothGattCharacteristic characteristic = find(session, op.uuid);
        if (characteristic == null || session.gatt == null) {
            return false;
        }
        if (op.kind == Op.READ) {
            return readCharacteristic(session.gatt, characteristic);
        }
        if (op.kind == Op.WRITE) {
            return writeCharacteristic(session.gatt, characteristic, op.data, op.writeType);
        }
        int props = characteristic.getProperties();
        boolean canIndicate = (props & BluetoothGattCharacteristic.PROPERTY_INDICATE) != 0;
        boolean canNotify = (props & BluetoothGattCharacteristic.PROPERTY_NOTIFY) != 0;
        if (!canIndicate && !canNotify) {
            return false;
        }
        boolean indicate = (canIndicate && !canNotify)
                || (canIndicate == canNotify && "2ad9".equals(shortUuid(characteristic.getUuid())));
        session.gatt.setCharacteristicNotification(characteristic, true);
        BluetoothGattDescriptor descriptor = characteristic.getDescriptor(CCCD);
        if (descriptor == null) {
            return false;
        }
        byte[] value = indicate
                ? BluetoothGattDescriptor.ENABLE_INDICATION_VALUE
                : BluetoothGattDescriptor.ENABLE_NOTIFICATION_VALUE;
        return writeDescriptor(session.gatt, descriptor, Arrays.copyOf(value, value.length));
    }

    private void complete(Session session, int token, Op op, int status, byte[] data) {
        if (session.current != op || session.opToken != token) {
            return;
        }
        session.opToken++;
        session.current = null;
        session.busy = false;
        emitOp(op, session.role, op.kind, status, data);
        if (op.requestId == -100 && status != BluetoothGatt.GATT_SUCCESS) {
            releaseStop(session);
        }
        Session target = session;
        handler.postDelayed(() -> pump(target), OP_GAP_MS);
    }

    private void emitOp(Op op, String role, int kind, int status, byte[] data) {
        String type = kind == Op.READ ? "read" : kind == Op.WRITE ? "write" : "subscribe";
        try {
            JSONObject json = new JSONObject();
            json.put("role", role == null ? "" : role);
            json.put("uuid", shortUuid(op.uuid));
            json.put("requestId", op.requestId);
            json.put("status", status);
            if (kind == Op.READ) {
                json.put("data", data == null ? "" : Base64.encodeToString(data, Base64.NO_WRAP));
            }
            send(type, json);
        } catch (JSONException ex) {
            Log.e(TAG, "op json", ex);
        }
    }

    @SuppressLint("MissingPermission")
    private void onConnected(Session session, int status) {
        if (session.closed) {
            return;
        }
        if (status != BluetoothGatt.GATT_SUCCESS) {
            drop(session, "status " + status);
            return;
        }
        sendConnection(session.role, "connected", session.address, "");
        boolean requested = false;
        if (session.gatt != null) {
            try {
                requested = session.gatt.requestMtu(64);
            } catch (RuntimeException ignored) {
                requested = false;
            }
        }
        if (!requested) {
            discover(session);
        } else {
            handler.postDelayed(() -> discover(session), 2000L);
        }
    }

    @SuppressLint("MissingPermission")
    private void discover(Session session) {
        if (session.closed || session.discoveryStarted || session.gatt == null) {
            return;
        }
        session.discoveryStarted = true;
        try {
            session.gatt.discoverServices();
        } catch (RuntimeException ex) {
            emitError(session.role, "Service discovery failed");
            drop(session, "service discovery failed");
        }
    }

    private void onServices(Session session, int status) {
        if (session.closed) {
            return;
        }
        if (status != BluetoothGatt.GATT_SUCCESS || session.gatt == null) {
            emitError(session.role, "Service discovery failed");
            drop(session, "service discovery failed");
            return;
        }
        session.ready = true;
        try {
            JSONObject json = new JSONObject();
            json.put("role", session.role);
            JSONArray items = new JSONArray();
            List<BluetoothGattService> services = session.gatt.getServices();
            if (services != null) {
                for (BluetoothGattService service : services) {
                    List<BluetoothGattCharacteristic> characteristics = service.getCharacteristics();
                    if (characteristics == null) {
                        continue;
                    }
                    for (BluetoothGattCharacteristic characteristic : characteristics) {
                        JSONObject item = new JSONObject();
                        item.put("service", shortUuid(service.getUuid()));
                        item.put("uuid", shortUuid(characteristic.getUuid()));
                        item.put("properties", characteristic.getProperties());
                        items.put(item);
                    }
                }
            }
            json.put("items", items);
            send("services", json);
        } catch (JSONException ex) {
            Log.e(TAG, "services json", ex);
        }
        pump(session);
    }

    private void onNotify(Session session, UUID uuid, byte[] value) {
        if (session.closed) {
            return;
        }
        byte[] copy = value == null ? new byte[0] : Arrays.copyOf(value, value.length);
        if (session.stopLatch != null
                && "2ad9".equals(shortUuid(uuid))
                && copy.length >= 2 && copy[0] == (byte) 0x80 && copy[1] == 0x08) {
            releaseStop(session);
        }
        try {
            JSONObject json = new JSONObject();
            json.put("role", session.role);
            json.put("uuid", shortUuid(uuid));
            json.put("data", Base64.encodeToString(copy, Base64.NO_WRAP));
            send("notify", json);
        } catch (JSONException ex) {
            Log.e(TAG, "notify json", ex);
        }
    }

    private void releaseStop(Session session) {
        CountDownLatch latch = session.stopLatch;
        session.stopLatch = null;
        if (latch != null) {
            latch.countDown();
        }
    }

    @SuppressLint("MissingPermission")
    private void drop(Session session, String detail) {
        if (session.closed) {
            return;
        }
        session.closed = true;
        releaseStop(session);
        closeGatt(session);
        sessions.remove(session.role);
        Op leftover = session.current;
        session.current = null;
        session.busy = false;
        session.opToken++;
        if (leftover != null) {
            emitOp(leftover, session.role, leftover.kind, -3, null);
        }
        while (!session.queue.isEmpty()) {
            Op pending = session.queue.removeFirst();
            emitOp(pending, session.role, pending.kind, -3, null);
        }
        sendConnection(session.role, "disconnected", session.address, detail == null ? "link lost" : detail);
    }

    @SuppressLint("MissingPermission")
    private void closeGatt(Session session) {
        BluetoothGatt gatt = session.gatt;
        session.gatt = null;
        if (gatt == null) {
            return;
        }
        try {
            gatt.disconnect();
        } catch (RuntimeException ignored) {
        }
        try {
            gatt.close();
        } catch (RuntimeException ignored) {
        }
    }

    private BluetoothGattCharacteristic find(Session session, String uuid) {
        if (session.gatt == null || uuid == null) {
            return null;
        }
        UUID id;
        try {
            id = parseUuid(uuid);
        } catch (IllegalArgumentException ex) {
            return null;
        }
        List<BluetoothGattService> services = session.gatt.getServices();
        if (services == null) {
            return null;
        }
        for (BluetoothGattService service : services) {
            BluetoothGattCharacteristic characteristic = service.getCharacteristic(id);
            if (characteristic != null) {
                return characteristic;
            }
        }
        return null;
    }

    @SuppressLint("MissingPermission")
    private boolean readCharacteristic(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic) {
        return gatt.readCharacteristic(characteristic);
    }

    @SuppressWarnings("deprecation")
    @SuppressLint("MissingPermission")
    private boolean writeCharacteristic(
            BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] data, int writeType) {
        if (Build.VERSION.SDK_INT >= 33) {
            return Api33.writeCharacteristic(gatt, characteristic, data, writeType);
        }
        characteristic.setWriteType(writeType);
        characteristic.setValue(data);
        return gatt.writeCharacteristic(characteristic);
    }

    @SuppressWarnings("deprecation")
    @SuppressLint("MissingPermission")
    private boolean writeDescriptor(BluetoothGatt gatt, BluetoothGattDescriptor descriptor, byte[] value) {
        if (Build.VERSION.SDK_INT >= 33) {
            return Api33.writeDescriptor(gatt, descriptor, value);
        }
        descriptor.setValue(value);
        return gatt.writeDescriptor(descriptor);
    }

    private void sendConnection(String role, String state, String address, String detail) {
        try {
            JSONObject json = new JSONObject();
            json.put("role", role == null ? "" : role);
            json.put("state", state);
            json.put("address", address == null ? "" : address);
            json.put("message", detail == null ? "" : detail);
            send("connection", json);
        } catch (JSONException ex) {
            Log.e(TAG, "connection json", ex);
        }
    }

    private void emitError(String role, String message) {
        try {
            JSONObject json = new JSONObject();
            json.put("role", role == null ? "" : role);
            json.put("message", message == null ? "" : message);
            send("error", json);
        } catch (JSONException ex) {
            Log.e(TAG, "error json", ex);
        }
        Log.w(TAG, (role == null ? "" : role + " ") + message);
    }

    private void send(String type, JSONObject json) {
        try {
            json.put("type", type);
        } catch (JSONException ex) {
            Log.e(TAG, "type json", ex);
            return;
        }
        final String text = json.toString();
        main.post(() -> unitySend(text));
    }

    private static void unitySend(String text) {
        try {
            Class<?> player = Class.forName("com.unity3d.player.UnityPlayer");
            Method method = player.getMethod("UnitySendMessage", String.class, String.class, String.class);
            method.invoke(null, RECEIVER, METHOD, text);
        } catch (Exception ex) {
            Log.e(TAG, "UnitySendMessage failed", ex);
        }
    }

    private static Context activity() {
        try {
            Class<?> player = Class.forName("com.unity3d.player.UnityPlayer");
            Object activity = player.getField("currentActivity").get(null);
            if (activity instanceof Context) {
                return (Context) activity;
            }
        } catch (Exception ex) {
            Log.e(TAG, "No Unity activity", ex);
        }
        return null;
    }

    private static BluetoothAdapter adapter(Context context) {
        BluetoothManager manager = (BluetoothManager) context.getSystemService(Context.BLUETOOTH_SERVICE);
        if (manager == null) {
            return null;
        }
        return manager.getAdapter();
    }

    private static UUID parseUuid(String uuid) {
        String text = uuid.trim().toLowerCase(Locale.US);
        if (text.length() == 4) {
            text = "0000" + text + "-0000-1000-8000-00805f9b34fb";
        }
        return UUID.fromString(text);
    }

    private static String shortUuid(String uuid) {
        if (uuid == null) {
            return "";
        }
        String text = uuid.toLowerCase(Locale.US);
        String suffix = "-0000-1000-8000-00805f9b34fb";
        if (text.length() == 36 && text.startsWith("0000") && text.endsWith(suffix)) {
            return text.substring(4, 8);
        }
        return text;
    }

    private static String shortUuid(UUID uuid) {
        return uuid == null ? "" : shortUuid(uuid.toString());
    }

    @SuppressWarnings("deprecation")
    private static byte[] valueOf(BluetoothGattCharacteristic characteristic) {
        byte[] value = characteristic.getValue();
        return value == null ? new byte[0] : Arrays.copyOf(value, value.length);
    }

    private final class RoleCallback extends BluetoothGattCallback {
        private final Session session;

        RoleCallback(Session session) {
            this.session = session;
        }

        @Override
        public void onConnectionStateChange(BluetoothGatt gatt, int status, int newState) {
            handler.post(() -> {
                if (newState == BluetoothProfile.STATE_CONNECTED) {
                    onConnected(session, status);
                } else if (newState == BluetoothProfile.STATE_DISCONNECTED) {
                    String detail = status == BluetoothGatt.GATT_SUCCESS ? "link lost" : "status " + status;
                    drop(session, detail);
                }
            });
        }

        @Override
        public void onMtuChanged(BluetoothGatt gatt, int mtu, int status) {
            handler.post(() -> discover(session));
        }

        @Override
        public void onServicesDiscovered(BluetoothGatt gatt, int status) {
            handler.post(() -> onServices(session, status));
        }

        @Override
        public void onCharacteristicRead(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, int status) {
            if (Build.VERSION.SDK_INT >= 33) {
                return;
            }
            byte[] value = valueOf(characteristic);
            handler.post(() -> {
                Op op = session.current;
                if (op != null && op.kind == Op.READ) {
                    complete(session, session.opToken, op, status, value);
                }
            });
        }

        @Override
        public void onCharacteristicRead(
                BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value, int status) {
            byte[] copy = value == null ? new byte[0] : Arrays.copyOf(value, value.length);
            handler.post(() -> {
                Op op = session.current;
                if (op != null && op.kind == Op.READ) {
                    complete(session, session.opToken, op, status, copy);
                }
            });
        }

        @Override
        public void onCharacteristicWrite(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, int status) {
            handler.post(() -> {
                Op op = session.current;
                if (op != null && op.kind == Op.WRITE) {
                    complete(session, session.opToken, op, status, null);
                }
            });
        }

        @Override
        public void onDescriptorWrite(BluetoothGatt gatt, BluetoothGattDescriptor descriptor, int status) {
            handler.post(() -> {
                Op op = session.current;
                if (op != null && op.kind == Op.SUBSCRIBE) {
                    complete(session, session.opToken, op, status, null);
                }
            });
        }

        @Override
        public void onCharacteristicChanged(BluetoothGatt gatt, BluetoothGattCharacteristic characteristic) {
            if (Build.VERSION.SDK_INT >= 33) {
                return;
            }
            UUID uuid = characteristic.getUuid();
            byte[] value = valueOf(characteristic);
            handler.post(() -> onNotify(session, uuid, value));
        }

        @Override
        public void onCharacteristicChanged(
                BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] value) {
            UUID uuid = characteristic.getUuid();
            byte[] copy = value == null ? new byte[0] : Arrays.copyOf(value, value.length);
            handler.post(() -> onNotify(session, uuid, copy));
        }
    }

    private static final class Session {
        final String role;
        final String address;
        final ArrayDeque<Op> queue = new ArrayDeque<Op>();
        BluetoothGatt gatt;
        BluetoothGattCallback callback;
        Op current;
        boolean ready;
        boolean busy;
        boolean closed;
        boolean discoveryStarted;
        boolean stopOnQuit;
        String stopUuid;
        byte[] stopPayload;
        CountDownLatch stopLatch;
        int opToken;

        Session(String role, String address) {
            this.role = role;
            this.address = address;
        }

    }

    private static final class Op {
        static final int READ = 1;
        static final int WRITE = 2;
        static final int SUBSCRIBE = 3;

        final int kind;
        final String uuid;
        final byte[] data;
        final int writeType;
        final int requestId;

        private Op(int kind, String uuid, byte[] data, int writeType, int requestId) {
            this.kind = kind;
            this.uuid = uuid;
            this.data = data;
            this.writeType = writeType;
            this.requestId = requestId;
        }

        static Op read(String uuid, int requestId) {
            return new Op(READ, uuid, null, 0, requestId);
        }

        static Op subscribe(String uuid, int requestId) {
            return new Op(SUBSCRIBE, uuid, null, 0, requestId);
        }

        static Op write(String uuid, byte[] data, boolean withResponse, int requestId) {
            int type = withResponse
                    ? BluetoothGattCharacteristic.WRITE_TYPE_DEFAULT
                    : BluetoothGattCharacteristic.WRITE_TYPE_NO_RESPONSE;
            return new Op(WRITE, uuid, data, type, requestId);
        }
    }

    /** Loaded only on API 33+, where the byte-array GATT writes exist. */
    private static final class Api33 {
        @SuppressLint("MissingPermission")
        static boolean writeCharacteristic(
                BluetoothGatt gatt, BluetoothGattCharacteristic characteristic, byte[] data, int writeType) {
            return gatt.writeCharacteristic(characteristic, data, writeType) == BluetoothStatusCodes.SUCCESS;
        }

        @SuppressLint("MissingPermission")
        static boolean writeDescriptor(BluetoothGatt gatt, BluetoothGattDescriptor descriptor, byte[] value) {
            return gatt.writeDescriptor(descriptor, value) == BluetoothStatusCodes.SUCCESS;
        }
    }
}
