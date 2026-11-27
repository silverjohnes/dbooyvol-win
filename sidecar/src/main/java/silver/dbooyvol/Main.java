package silver.dbooyvol;

import java.io.*;
import java.nio.charset.StandardCharsets;
import java.util.LinkedHashMap;
import java.util.Map;
import java.util.concurrent.ExecutorService;
import java.util.concurrent.Executors;

/**
 * NDJSON request/response loop over stdin/stdout.
 *   -> {"id":1,"method":"query","params":{...}}
 *   <- {"id":1,"ok":true,"result":{...}}
 */
public final class Main {
    private static final Object OUT_LOCK = new Object();
    private static final Engine ENGINE = new Engine();

    public static void main(String[] args) throws Exception {
        BufferedReader in = new BufferedReader(new InputStreamReader(System.in, StandardCharsets.UTF_8));
        PrintStream out = new PrintStream(new FileOutputStream(FileDescriptor.out), true, "UTF-8");
        // Queries in different tabs (and on different connections) run in
        // parallel; the engine serializes statements per JDBC connection.
        ExecutorService pool = Executors.newCachedThreadPool(r -> {
            Thread t = new Thread(r, "jdbc-worker");
            t.setDaemon(true);
            return t;
        });

        String line;
        while ((line = in.readLine()) != null) {
            if (line.trim().isEmpty()) continue;
            final String raw = line;
            // cancel and the background row count must not wait behind the
            // running query; both use their own resources.
            if (raw.contains("\"method\":\"cancel\"") || raw.contains("\"method\":\"cancelConn\"")
                    || raw.contains("\"method\":\"count\"")
                    || raw.contains("\"method\":\"updateRows\"")
                    || raw.contains("\"method\":\"primaryKeys\"")
                    || raw.contains("\"method\":\"tableMeta\"")) {
                Thread t = new Thread(() -> handle(raw, out), "jdbc-side");
                t.setDaemon(true);
                t.start();
            } else {
                pool.submit(() -> handle(raw, out));
            }
        }
        pool.shutdown();
        try { pool.awaitTermination(30, java.util.concurrent.TimeUnit.SECONDS); } catch (InterruptedException ignored) { }
        ENGINE.shutdown();
    }

    private static void handle(String raw, PrintStream out) {
        Object id = null;
        Map<String, Object> response = new LinkedHashMap<>();
        try {
            Map<String, Object> req = Json.obj(Json.parse(raw));
            id = req.get("id");
            String method = Json.str(req.get("method"));
            Map<String, Object> params = Json.obj(req.get("params"));

            Map<String, Object> result;
            if ("connect".equals(method)) result = ENGINE.connect(params);
            else if ("disconnect".equals(method)) result = ENGINE.disconnect(params);
            else if ("tree".equals(method)) result = ENGINE.tree(params);
            else if ("query".equals(method)) result = ENGINE.query(params);
            else if ("fetch".equals(method)) result = ENGINE.fetch(params);
            else if ("cancel".equals(method)) result = ENGINE.cancel(params);
            else if ("cancelConn".equals(method)) result = ENGINE.cancelConn(params);
            else if ("count".equals(method)) result = ENGINE.count(params);
            else if ("primaryKeys".equals(method)) result = ENGINE.primaryKeys(params);
            else if ("tableMeta".equals(method)) result = ENGINE.tableMeta(params);
            else if ("updateRows".equals(method)) result = ENGINE.updateRows(params);
            else if ("closeCursor".equals(method)) result = ENGINE.closeCursor(params);
            else if ("ping".equals(method)) { result = new LinkedHashMap<>(); result.put("pong", true); }
            else throw new IllegalArgumentException("Unknown method: " + method);

            response.put("id", id);
            response.put("ok", true);
            response.put("result", result);
        } catch (Throwable t) {
            response.put("id", id);
            response.put("ok", false);
            response.put("error", error(t));
        }
        synchronized (OUT_LOCK) {
            out.println(Json.write(response));
            out.flush();
        }
    }

    private static Map<String, Object> error(Throwable t) {
        Map<String, Object> e = new LinkedHashMap<>();
        Throwable root = t;
        while (root.getCause() != null && root.getCause() != root) root = root.getCause();

        e.put("message", root.getMessage() == null ? root.toString() : root.getMessage());
        e.put("class", root.getClass().getSimpleName());

        Throwable cur = t;
        int guard = 0;
        while (cur != null && guard++ < 6) {
            if (cur instanceof java.sql.SQLException) {
                java.sql.SQLException se = (java.sql.SQLException) cur;
                e.put("sqlState", se.getSQLState());
                e.put("code", se.getErrorCode());
                if (se.getMessage() != null) e.put("message", se.getMessage());
                break;
            }
            cur = cur.getCause();
        }
        return e;
    }
}
