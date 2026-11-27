package silver.dbooyvol;

import java.io.File;
import java.math.BigDecimal;
import java.net.URL;
import java.net.URLClassLoader;
import java.sql.*;
import java.time.*;
import java.time.format.DateTimeFormatter;
import java.util.*;
import java.util.Base64;
import java.util.concurrent.ConcurrentHashMap;
import java.util.regex.Matcher;
import java.util.regex.Pattern;

/**
 * JDBC engine. One process, many connections. All result values are returned
 * as strings (or null) so nothing loses precision or timezone information
 * on the way through JSON.
 */
public final class Engine {
    private static final int PAGE = 200;
    private static final int MAX_ROWS = 100_000;

    private final Map<String, Conn> conns = new ConcurrentHashMap<>();
    private final Map<String, Cursor> cursors = new ConcurrentHashMap<>();
    /** One lock per JDBC connection: statements on the same connection must not
     *  run at the same time, while different connections may run in parallel. */
    private final Map<String, Object> connLocks = new ConcurrentHashMap<>();
    /** Statement currently executing per connection, so Stop can cancel it
     *  before the first page (and its queryId) has been returned. */
    private final Map<String, Statement> activeStatements = new ConcurrentHashMap<>();
    private int seq = 0;

    private Object connLock(String connId) {
        return connLocks.computeIfAbsent(connId == null ? "" : connId, k -> new Object());
    }

    /** Driver jars already opened. Re-registering the same jar in a new
     *  classloader duplicates driver classes; for OCI the JVM then refuses to
     *  load the same native library twice ("already loaded in another
     *  classloader"). One classloader per jar is enough. */
    private static final Set<String> loadedDriverJars = new HashSet<>();

    private static final class Conn {
        final Connection c;
        /** Kept so a counting query can use its own connection while a result
         *  cursor is still open on this one. */
        final String url;
        final Properties props;
        Conn(Connection c, String url, Properties props) {
            this.c = c;
            this.url = url;
            this.props = props;
        }
    }

    private static final class Cursor {
        final String connId;
        final Statement st;
        final ResultSet rs;
        final ResultSetMetaData md;
        final List<Map<String, Object>> columns;
        int fetched = 0;
        boolean done = false;
        Cursor(String connId, Statement st, ResultSet rs, ResultSetMetaData md,
               List<Map<String, Object>> columns) {
            this.connId = connId; this.st = st; this.rs = rs; this.md = md; this.columns = columns;
        }
    }

    // ---------------------------------------------------------------- connect

    public Map<String, Object> connect(Map<String, Object> p) throws Exception {
        String kind = Json.str(p.get("kind"));
        String host = Json.str(p.get("host"));
        int port = Json.intOf(p.get("port"),
                "oracle".equals(kind) ? 1521 : "clickhouse".equals(kind) ? 8123 : "sqlite".equals(kind) ? 0 : 5432);
        String database = Json.str(p.get("database"));
        String user = Json.str(p.get("user"));
        String password = Json.str(p.get("password"));
        String driverPath = Json.str(p.get("driverPath"));
        boolean sid = Json.boolOf(p.get("oracleSid"));
        String urlOverride = Json.str(p.get("url"));

        String oracleMode = Json.str(p.get("oracleMode"));
        String oracleAlias = Json.str(p.get("oracleAlias"));
        String tnsAdmin = Json.str(p.get("tnsAdmin"));
        String ldapHost = Json.str(p.get("ldapHost"));
        int ldapPort = Json.intOf(p.get("ldapPort"), 389);
        String ldapContext = Json.str(p.get("ldapContext"));
        boolean ldapSsl = Json.boolOf(p.get("ldapSsl"));
        String authServices = Json.str(p.get("oracleAuthServices"));
        String mode = isEmpty(oracleMode) ? "easy" : oracleMode.toLowerCase(Locale.ROOT);

        // A folder given as the driver path may also be a whole Instant Client.
        if ("oracle".equals(kind) && isEmpty(tnsAdmin) && !isEmpty(driverPath)) {
            File d = new File(driverPath.trim());
            File admin = new File(d, "network/admin");
            if (d.isDirectory() && admin.isDirectory()) tnsAdmin = admin.getAbsolutePath();
        }

        if ("oracle".equals(kind) && !isEmpty(tnsAdmin)) {
            File dir = new File(tnsAdmin.trim());
            if (!dir.isDirectory()) {
                throw new IllegalArgumentException("TNS_ADMIN: каталог не найден — " + tnsAdmin);
            }
            tnsAdmin = dir.getAbsolutePath();
            // Deliberately no System.setProperty here: the value goes into the
            // connection properties, so one profile's TNS_ADMIN cannot leak
            // into the next connection in the same JVM.
        }

        loadDrivers(driverPath);
        if ("postgres".equals(kind)) loadBundled("postgresql.jar");
        if ("sqlite".equals(kind)) loadBundled("sqlite-jdbc.jar");
        if ("clickhouse".equals(kind)) {
            // ClickHouse logging goes through slf4j; both jars must be in one
            // classloader, otherwise the driver cannot see the logging API.
            loadBundledAll("clickhouse-jdbc.jar", "slf4j-api.jar", "slf4j-nop.jar", "antlr4-runtime.jar");
        }
        if ("oracle".equals(kind) && (driverPath == null || driverPath.trim().isEmpty())) {
            throw new IllegalArgumentException("Для Oracle укажите путь к ojdbc8.jar / ojdbc11.jar в профиле подключения");
        }

        String url;
        Properties props = new Properties();
        if (urlOverride != null && !urlOverride.isEmpty()) {
            url = urlOverride;
            props.put("user", user == null ? "" : user);
            props.put("password", password == null ? "" : password);
        } else if ("oracle".equals(kind)) {
            if ("tns".equals(mode)) {
                if (isEmpty(oracleAlias)) {
                    throw new IllegalArgumentException("Укажите TNS-алиас (имя из tnsnames.ora)");
                }
                url = "jdbc:oracle:thin:@" + oracleAlias.trim();
            } else if ("ldap".equals(mode)) {
                url = ldapURL(oracleAlias, ldapHost, ldapPort, ldapContext, ldapSsl);
            } else if (sid) {
                url = "jdbc:oracle:thin:@" + host + ":" + port + ":" + database;
            } else {
                url = "jdbc:oracle:thin:@//" + host + ":" + port + "/" + database;
            }
            props.put("oracle.jdbc.ReadTimeout", "0");
            props.put("oracle.jdbc.defaultNChar", "true");
            props.put("oracle.jdbc.timezoneAsRegion", "false");
            props.put("v$session.program", "DBooyvol");
            if (!isEmpty(tnsAdmin)) {
                // Per-connection value, so one profile's TNS_ADMIN does not leak into another.
                props.put("oracle.net.tns_admin", new File(tnsAdmin.trim()).getAbsolutePath());
            }
            if (!isEmpty(authServices)) {
                String services = authServices.toUpperCase(Locale.ROOT);
                if (services.contains("RADIUS")) {
                    // Thin driver takes security settings from Properties, not sqlnet.ora.
                    props.put("oracle.net.authentication_services", "(RADIUS)");
                }
            }
        } else if ("sqlite".equals(kind)) {
            if (isEmpty(database)) {
                throw new IllegalArgumentException("Укажите файл базы SQLite (.db / .sqlite)");
            }
            url = "jdbc:sqlite:" + new File(database.trim()).getAbsolutePath();
        } else if ("clickhouse".equals(kind)) {
            url = "jdbc:clickhouse://" + host + ":" + port + "/" + (database == null ? "" : database);
            props.put("socket_timeout", "30000");
            props.put("connection_timeout", "15000");
            props.put("compress", "1");
        } else {
            url = "jdbc:postgresql://" + host + ":" + port + "/" + database;
            props.put("ApplicationName", "DBooyvol");
            props.put("sslmode", "prefer");
            props.put("connectTimeout", "15");
            // Keeps idle sessions from being silently dropped by a firewall or the server.
            props.put("tcpKeepAlive", "true");
        }
        if (user != null) props.put("user", user);
        if (password != null) props.put("password", password);

        DriverManager.setLoginTimeout(20);
        Connection c;
        try {
            c = DriverManager.getConnection(url, props);
        } catch (SQLException e) {
            String message = e.getMessage() == null ? "" : e.getMessage();
            if (message.contains("No suitable driver")) {
                throw new SQLException(
                    "Не найден JDBC-драйвер для " + url
                        + ". Укажите путь к jar в профиле подключения (файл должен существовать).",
                    e.getSQLState(), e.getErrorCode(), e);
            }
            if (message.contains("could not resolve the connect identifier")) {
                if ("ldap".equals(mode)) {
                    throw new SQLException(
                        "LDAP/OID не разрешил алиас \"" + oracleAlias + "\". Проверьте ldap.ora "
                            + "(DIRECTORY_SERVERS, DEFAULT_ADMIN_CONTEXT) и имя сервиса.",
                        e.getSQLState(), e.getErrorCode(), e);
                }
                if ("tns".equals(mode)) {
                    throw new SQLException(
                        "TNS-алиас \"" + oracleAlias + "\" не найден. Проверьте tnsnames.ora "
                            + "в TNS_ADMIN и имя алиаса.",
                        e.getSQLState(), e.getErrorCode(), e);
                }
            }
            throw e;
        }
        // ClickHouse has no transactions and SQLite rejects changing read-only
        // after the connection is open; neither is fatal for the app.
        if (!"clickhouse".equals(kind) && !"sqlite".equals(kind)) {
            c.setAutoCommit(false);
        } else {
            try { c.setAutoCommit(false); } catch (Exception ignored) { }
        }
        try { c.setReadOnly(true); } catch (Exception ignored) { }

        String id = "c" + (++seq);
        conns.put(id, new Conn(c, url, props));
        Map<String, Object> r = new LinkedHashMap<>();
        r.put("connId", id);
        r.put("serverVersion", c.getMetaData().getDatabaseProductVersion());
        r.put("driver", c.getMetaData().getDriverName() + " " + c.getMetaData().getDriverVersion());
        r.put("user", c.getMetaData().getUserName());
        return r;
    }

    private static boolean isEmpty(String s) {
        return s == null || s.trim().isEmpty();
    }

    /**
     * The thin driver understands the LDAP URL form directly. It does NOT read
     * ldap.ora or NAMES.DIRECTORY_PATH for a bare alias, so the app builds this
     * URL from the values that are in ldap.ora.
     */
    private static String ldapURL(String alias, String host, int port, String context, boolean ssl)
            throws IllegalArgumentException {
        if (isEmpty(alias)) throw new IllegalArgumentException("Укажите имя сервиса (LDAP alias)");
        String a = alias.trim();
        if (a.regionMatches(true, 0, "ldap://", 0, 7)
                || a.regionMatches(true, 0, "ldaps://", 0, 8)
                || a.startsWith("(")) {
            return "jdbc:oracle:thin:@" + a;
        }
        if (isEmpty(host)) {
            throw new IllegalArgumentException("Укажите LDAP-сервер (ldap.ora: DIRECTORY_SERVERS)");
        }
        StringBuilder sb = new StringBuilder("jdbc:oracle:thin:@");
        sb.append(ssl ? "ldaps" : "ldap").append("://").append(host.trim()).append(':').append(port);
        sb.append('/').append(a);
        String ctx = context == null ? "" : context.trim();
        while (ctx.startsWith(",")) ctx = ctx.substring(1).trim();
        if (!ctx.isEmpty()) {
            if (!ctx.toLowerCase(Locale.ROOT).contains("cn=oraclecontext")) {
                ctx = "cn=OracleContext," + ctx;
            }
            sb.append(',').append(ctx);
        }
        return sb.toString();
    }

    private static synchronized void loadDrivers(String path) throws Exception {
        if (path == null || path.trim().isEmpty()) return;
        String[] parts = path.split(File.pathSeparator);
        List<URL> urls = new ArrayList<>();
        for (String part : parts) {
            String t = part.trim();
            if (t.isEmpty()) continue;
            File f = new File(t);
            if (!f.exists()) throw new IllegalArgumentException("JDBC driver not found: " + t);
            if (f.isDirectory()) {
                // Instant Client folder: pick one JDBC jar, never the dylibs.
                File[] jars = f.listFiles((dir, name) -> {
                    String n = name.toLowerCase(Locale.ROOT);
                    return n.startsWith("ojdbc") && n.endsWith(".jar");
                });
                if (jars == null || jars.length == 0) {
                    throw new IllegalArgumentException("В папке нет ojdbc*.jar: " + t);
                }
                urls.add(bestJdbcJar(jars).toURI().toURL());
            } else {
                urls.add(f.toURI().toURL());
            }
        }
        if (urls.isEmpty()) return;
        StringBuilder keyBuilder = new StringBuilder();
        for (URL url : urls) keyBuilder.append(url).append('|');
        String key = keyBuilder.toString();
        if (!loadedDriverJars.add(key)) return;

        URLClassLoader cl = new URLClassLoader(urls.toArray(new URL[0]), Engine.class.getClassLoader());
        for (Driver d : ServiceLoader.load(Driver.class, cl)) {
            try { DriverManager.registerDriver(new DriverShim(d)); } catch (Exception ignored) { }
        }
    }

    /**
     * Picks a single driver jar from a folder. Loading several ojdbc*.jar
     * copies would register duplicate Oracle classes; with OCI the JVM then
     * refuses to load the same native library from a second classloader
     * ("already loaded in another classloader"). The thin driver needs one jar.
     */
    private static File bestJdbcJar(File[] jars) {
        File best = null;
        int bestRank = Integer.MIN_VALUE;
        for (File jar : jars) {
            String n = jar.getName().toLowerCase(Locale.ROOT);
            if (n.contains("-sources") || n.contains("-javadoc")) continue;
            int rank = 0;
            Matcher m = Pattern.compile("ojdbc(\\d+)").matcher(n);
            if (m.find()) {
                try { rank = Integer.parseInt(m.group(1)); } catch (NumberFormatException ignored) { }
            }
            // Prefer the regular jar over the debug (_g) build of the same version.
            if (n.contains("_g")) rank -= 1000;
            if (best == null || rank > bestRank) {
                best = jar;
                bestRank = rank;
            }
        }
        return best != null ? best : jars[0];
    }

    /** Driver shipped next to sidecar.jar (PostgreSQL works out of the box). */    private static void loadBundled(String name) {
        try {
            File self = new File(Engine.class.getProtectionDomain().getCodeSource().getLocation().toURI());
            File dir = self.isDirectory() ? self : self.getParentFile();
            if (dir == null) return;
            File jar = new File(dir, name);
            if (jar.exists()) loadDrivers(jar.getPath());
        } catch (Exception ignored) { }
    }

    /** Loads several bundled jars into one classloader, so a driver can see its
     *  logging or optional dependencies. Missing jars are skipped. */
    private static void loadBundledAll(String... names) {
        try {
            File self = new File(Engine.class.getProtectionDomain().getCodeSource().getLocation().toURI());
            File dir = self.isDirectory() ? self : self.getParentFile();
            if (dir == null) return;
            StringBuilder joined = new StringBuilder();
            for (String name : names) {
                File jar = new File(dir, name);
                if (!jar.exists()) continue;
                if (joined.length() > 0) joined.append(File.pathSeparator);
                joined.append(jar.getPath());
            }
            if (joined.length() > 0) loadDrivers(joined.toString());
        } catch (Exception ignored) { }
    }

    /** Wrapper so drivers from an isolated classloader survive DriverManager lookups. */
    private static final class DriverShim implements Driver {
        private final Driver d;
        DriverShim(Driver d) { this.d = d; }
        public Connection connect(String url, Properties info) throws SQLException { return d.connect(url, info); }
        public boolean acceptsURL(String url) throws SQLException { return d.acceptsURL(url); }
        public DriverPropertyInfo[] getPropertyInfo(String url, Properties info) throws SQLException { return d.getPropertyInfo(url, info); }
        public int getMajorVersion() { return d.getMajorVersion(); }
        public int getMinorVersion() { return d.getMinorVersion(); }
        public boolean jdbcCompliant() { return d.jdbcCompliant(); }
        public java.util.logging.Logger getParentLogger() throws SQLFeatureNotSupportedException {
            return d.getParentLogger();
        }
    }

    private Connection conn(String id) throws SQLException {
        Conn c = conns.get(id);
        if (c == null) throw new SQLException("Not connected");
        if (c.c.isClosed()) {
            conns.remove(id);
            throw new SQLException("Connection is closed");
        }
        return c.c;
    }

    public Map<String, Object> disconnect(Map<String, Object> p) {
        String id = Json.str(p.get("connId"));
        Conn c = conns.remove(id);
        if (c != null) {
            for (Map.Entry<String, Cursor> e : cursors.entrySet()) {
                if (e.getValue().st != null) { try { e.getValue().st.cancel(); } catch (Exception ignored) { } }
            }
            try { c.c.rollback(); } catch (Exception ignored) { }
            try { c.c.close(); } catch (Exception ignored) { }
        }
        Map<String, Object> r = new LinkedHashMap<>();
        r.put("closed", true);
        return r;
    }

    // ------------------------------------------------------------------- tree

    public Map<String, Object> tree(Map<String, Object> p) throws Exception {
        String connId = Json.str(p.get("connId"));
        synchronized (connLock(connId)) {
            return treeLocked(p);
        }
    }

    private Map<String, Object> treeLocked(Map<String, Object> p) throws Exception {
        Connection c = conn(p.get("connId") == null ? null : Json.str(p.get("connId")));
        List<String> path = new ArrayList<>();
        for (Object o : Json.arr(p.get("path"))) path.add(String.valueOf(o));
        DatabaseMetaData md = c.getMetaData();
        String kind = Json.str(p.get("kind"));

        List<Map<String, Object>> nodes = new ArrayList<>();
        try {

        if (path.isEmpty()) {
            if ("sqlite".equals(kind)) {
                // A SQLite file has a single schema; the file itself is the database.
                nodes.add(node("main", "main", "schema", true, true));
            } else {
            List<String> names = new ArrayList<>();
            try (ResultSet rs = md.getSchemas()) {
                while (rs.next()) names.add(rs.getString(1));
            }
            Collections.sort(names);
            if (names.isEmpty() && "clickhouse".equals(kind)) {
                String db = Json.str(p.get("database"));
                if (!isEmpty(db)) names.add(db.trim());
            }
            String self = null;
            try { self = md.getUserName(); } catch (Exception ignored) { }
            for (String n : names) {
                if ("postgres".equals(kind)) {
                    if (n.startsWith("pg_") || n.equals("information_schema")) continue;
                }
                nodes.add(node(n, n, "schema", true, self != null && n.equalsIgnoreCase(self)));
            }
            }
        } else if (path.size() == 1) {
            String schema = path.get(0);
            nodes.add(node(schema + "|Tables", "Таблицы", "folder", true, false));
            nodes.add(node(schema + "|Views", "Представления", "folder", true, false));
        } else if (path.size() == 2) {
            String schema = path.get(0);
            String folder = path.get(1);
            String[] types = folder.contains("Views") ? new String[]{"VIEW", "MATERIALIZED VIEW"} : new String[]{"TABLE"};
            // SQLite reports no schema name in its metadata.
            String schemaArg = "sqlite".equals(kind) ? null : schema;
            List<String> names = new ArrayList<>();
            try (ResultSet rs = md.getTables(null, schemaArg, "%", types)) {
                while (rs.next()) {
                    String t = rs.getString("TABLE_NAME");
                    if (t != null) names.add(t);
                }
            }
            Collections.sort(names, String.CASE_INSENSITIVE_ORDER);
            for (String t : names) {
                nodes.add(node(schema + "|" + t, t, folder.contains("Views") ? "view" : "table", true, false));
            }
        } else if (path.size() == 3) {
            String schema = path.get(0);
            String table = path.get(2);
            String schemaArg = "sqlite".equals(kind) ? null : schema;
            List<Map<String, Object>> cols = new ArrayList<>();
            try (ResultSet rs = md.getColumns(null, schemaArg, table, "%")) {
                while (rs.next()) {
                    String name = rs.getString("COLUMN_NAME");
                    String type = rs.getString("TYPE_NAME");
                    int size = rs.getInt("COLUMN_SIZE");
                    Map<String, Object> col = new LinkedHashMap<>();
                    col.put("label", name);
                    col.put("detail", type + (size > 0 ? "(" + size + ")" : ""));
                    col.put("id", schema + "|" + table + "|" + name);
                    col.put("kind", "column");
                    col.put("hasChildren", false);
                    cols.add(col);
                }
            }
            nodes.addAll(cols);
        }

        Map<String, Object> r = new LinkedHashMap<>();
        r.put("nodes", nodes);
        return r;
        } catch (Exception e) {
            // A failed metadata call aborts the shared transaction on PostgreSQL;
            // without a rollback every later statement would fail with 25P02.
            try { c.rollback(); } catch (Exception ignored) { }
            throw e;
        }
    }

    private static Map<String, Object> node(String id, String label, String kind, boolean children, boolean primary) {
        Map<String, Object> n = new LinkedHashMap<>();
        n.put("id", id);
        n.put("label", label);
        n.put("kind", kind);
        n.put("hasChildren", children);
        n.put("primary", primary);
        return n;
    }

    // ------------------------------------------------------------------ query

    /** SQL with named parameters rewritten to `?`, and the names in the order
     *  the placeholders appear. */
    private static final class Bound {
        final String sql;
        final List<String> names;
        Bound(String sql, List<String> names) {
            this.sql = sql;
            this.names = names;
        }
    }

    /** Marker for a parameter the caller did not supply. */
    private static final Object MISSING = new Object();

    /**
     * Turns `:name` into `?`, ignoring strings, quoted identifiers, comments
     * and PostgreSQL dollar-quotes. `::type` casts and `:=` assignments are
     * not parameters. Throws before execution when a value is missing, which
     * is clearer than the driver's "not all variables bound".
     */
    private static Bound bindPlaceholders(String sql, Map<String, Object> values) throws SQLException {
        StringBuilder out = new StringBuilder(sql.length() + 8);
        List<String> names = new ArrayList<>();
        int n = sql.length();
        int i = 0;
        while (i < n) {
            char c = sql.charAt(i);
            if (c == '-' && i + 1 < n && sql.charAt(i + 1) == '-') {
                int end = sql.indexOf('\n', i);
                if (end < 0) end = n;
                out.append(sql, i, end);
                i = end;
                continue;
            }
            if (c == '/' && i + 1 < n && sql.charAt(i + 1) == '*') {
                int end = sql.indexOf("*/", i + 2);
                end = end < 0 ? n : end + 2;
                out.append(sql, i, end);
                i = end;
                continue;
            }
            if (c == '\'' || c == '"') {
                int end = i + 1;
                while (end < n) {
                    char d = sql.charAt(end);
                    if (d == c) {
                        if (end + 1 < n && sql.charAt(end + 1) == c) { end += 2; continue; }
                        end++;
                        break;
                    }
                    end++;
                }
                end = Math.min(end, n);
                out.append(sql, i, end);
                i = end;
                continue;
            }
            if (c == '$') {
                int tagEnd = i + 1;
                while (tagEnd < n && (Character.isLetterOrDigit(sql.charAt(tagEnd)) || sql.charAt(tagEnd) == '_')) tagEnd++;
                if (tagEnd < n && sql.charAt(tagEnd) == '$') {
                    String tag = sql.substring(i, tagEnd + 1);
                    int end = sql.indexOf(tag, tagEnd + 1);
                    end = end < 0 ? n : end + tag.length();
                    out.append(sql, i, end);
                    i = end;
                    continue;
                }
            }
            if (c == ':') {
                if (i + 1 < n && (sql.charAt(i + 1) == ':' || sql.charAt(i + 1) == '=')) {
                    out.append(sql, i, i + 2);
                    i += 2;
                    continue;
                }
                int j = i + 1;
                if (j < n && (Character.isLetter(sql.charAt(j)) || sql.charAt(j) == '_')) {
                    int k = j + 1;
                    while (k < n) {
                        char d = sql.charAt(k);
                        if (!(Character.isLetterOrDigit(d) || d == '_' || d == '$' || d == '#')) break;
                        k++;
                    }
                    String name = sql.substring(j, k);
                    if (lookup(values, name) == MISSING) {
                        throw new SQLException("Не задано значение параметра :" + name);
                    }
                    out.append('?');
                    names.add(name);
                    i = k;
                    continue;
                }
            }
            out.append(c);
            i++;
        }
        return new Bound(out.toString(), names);
    }

    private static Object lookup(Map<String, Object> values, String name) {
        if (values.containsKey(name)) return values.get(name);
        // Oracle bind names are case-insensitive; so is the prompt.
        for (Map.Entry<String, Object> e : values.entrySet()) {
            if (e.getKey().equalsIgnoreCase(name)) return e.getValue();
        }
        return MISSING;
    }

    private static void applyBinds(PreparedStatement ps, List<String> names,
                                   Map<String, Object> values, String kind) throws SQLException {
        for (int i = 0; i < names.size(); i++) {
            Object v = lookup(values, names.get(i));
            if (v == MISSING || v == null) {
                ps.setNull(i + 1, Types.VARCHAR);
            } else if ("postgres".equals(kind)) {
                // Send an untyped value so PostgreSQL can infer the column type
                // (a varchar parameter cannot be compared with a date column).
                ps.setObject(i + 1, String.valueOf(v), Types.OTHER);
            } else {
                ps.setString(i + 1, String.valueOf(v));
            }
        }
    }

    public Map<String, Object> query(Map<String, Object> p) throws Exception {
        String connId = Json.str(p.get("connId"));
        synchronized (connLock(connId)) {
            return queryLocked(p);
        }
    }

    private Map<String, Object> queryLocked(Map<String, Object> p) throws Exception {
        String connId = Json.str(p.get("connId"));
        String sql = Json.str(p.get("sql"));
        String kind = Json.str(p.get("kind"));
        Connection c = conn(connId);

        Statement st;
        boolean hasBinds;
        try {
            Bound bound = bindPlaceholders(sql, Json.obj(p.get("params")));
            hasBinds = !bound.names.isEmpty();
            if (!hasBinds) {
                st = c.createStatement(ResultSet.TYPE_FORWARD_ONLY, ResultSet.CONCUR_READ_ONLY);
            } else {
                PreparedStatement ps = c.prepareStatement(
                        bound.sql, ResultSet.TYPE_FORWARD_ONLY, ResultSet.CONCUR_READ_ONLY);
                applyBinds(ps, bound.names, Json.obj(p.get("params")), kind);
                st = ps;
            }
        } catch (SQLException e) {
            try { c.rollback(); } catch (Exception ignored) { }
            throw e;
        }
        try { st.setFetchSize("oracle".equals(kind) ? 500 : 1000); } catch (Exception ignored) { }
        long started = System.currentTimeMillis();
        activeStatements.put(connId, st);

        try {
        boolean hasResult = hasBinds ? ((PreparedStatement) st).execute() : st.execute(sql);
        if (!hasResult) {
            int updateCount = st.getUpdateCount();
            try { c.commit(); } catch (Exception ignored) { }
            st.close();
            Map<String, Object> r = new LinkedHashMap<>();
            r.put("updateCount", updateCount);
            r.put("columns", new ArrayList<>());
            r.put("rows", new ArrayList<>());
            r.put("truncated", false);
            r.put("durationMs", System.currentTimeMillis() - started);
            return r;
        }

        ResultSet rs = st.getResultSet();
        ResultSetMetaData md = rs.getMetaData();
        List<Map<String, Object>> columns = new ArrayList<>();
        for (int i = 1; i <= md.getColumnCount(); i++) {
            Map<String, Object> col = new LinkedHashMap<>();
            col.put("name", md.getColumnLabel(i));
            col.put("type", md.getColumnTypeName(i));
            col.put("className", md.getColumnClassName(i));
            col.put("jdbcType", md.getColumnType(i));
            col.put("precision", md.getPrecision(i));
            col.put("scale", md.getScale(i));
            col.put("nullable", md.isNullable(i) != ResultSetMetaData.columnNoNulls);
            try { col.put("table", md.getTableName(i)); } catch (Exception ignored) { }
            try { col.put("schema", md.getSchemaName(i)); } catch (Exception ignored) { }
            try { col.put("catalog", md.getCatalogName(i)); } catch (Exception ignored) { }
            columns.add(col);
        }

        String queryId = "q" + (++seq);
        Cursor cur = new Cursor(connId, st, rs, md, columns);
        cursors.put(queryId, cur);
        int pageSize = Math.max(1, Math.min(Json.intOf(p.get("count"), PAGE), MAX_ROWS));
        List<List<String>> rows = readPage(cur, pageSize);

        Map<String, Object> r = new LinkedHashMap<>();
        r.put("queryId", queryId);
        r.put("columns", columns);
        r.put("rows", rows);
        r.put("truncated", cur.done && cur.fetched >= MAX_ROWS);
        r.put("done", cur.done);
        r.put("durationMs", System.currentTimeMillis() - started);
        if (cur.done) closeCursor(queryId);
        return r;
        } catch (Exception e) {
            try { st.close(); } catch (Exception ignored) { }
            try { c.rollback(); } catch (Exception ignored) { }
            throw e;
        } finally {
            activeStatements.remove(connId, st);
        }
    }

    /**
     * Runs a counting statement on a short-lived second connection, so it never
     * touches the connection that is still streaming the main result.
     */
    public Map<String, Object> count(Map<String, Object> p) throws Exception {
        String connId = Json.str(p.get("connId"));
        String sql = Json.str(p.get("sql"));
        String kind = Json.str(p.get("kind"));
        Conn base = conns.get(connId);
        if (base == null) throw new SQLException("Нет соединения");
        if (isEmpty(sql)) throw new IllegalArgumentException("Пустой запрос");

        long started = System.currentTimeMillis();
        Connection c = DriverManager.getConnection(base.url, base.props);
        try {
            try { c.setReadOnly(true); } catch (Exception ignored) { }
            Bound bound = bindPlaceholders(sql, Json.obj(p.get("params")));
            try (Statement st = bound.names.isEmpty()
                    ? c.createStatement()
                    : prepared(c, bound, Json.obj(p.get("params")), kind)) {
                st.setQueryTimeout(600);
                try (ResultSet rs = bound.names.isEmpty()
                        ? st.executeQuery(sql)
                        : ((PreparedStatement) st).executeQuery()) {
                    long rows = rs.next() ? rs.getLong(1) : 0L;
                    Map<String, Object> r = new LinkedHashMap<>();
                    r.put("count", rows);
                    r.put("durationMs", System.currentTimeMillis() - started);
                    return r;
                }
            }
        } finally {
            try { c.close(); } catch (Exception ignored) { }
        }
    }

    private static PreparedStatement prepared(Connection c, Bound bound,
                                              Map<String, Object> values, String kind) throws SQLException {
        PreparedStatement ps = c.prepareStatement(bound.sql);
        applyBinds(ps, bound.names, values, kind);
        return ps;
    }

    private static List<List<String>> readPage(Cursor cur, int count) throws SQLException {
        List<List<String>> rows = new ArrayList<>();
        int n = cur.md.getColumnCount();
        while (rows.size() < count && cur.fetched < MAX_ROWS) {
            if (!cur.rs.next()) { cur.done = true; break; }
            List<String> row = new ArrayList<>(n);
            for (int i = 1; i <= n; i++) {
                row.add(value(cur.rs, i, cur.md));
            }
            rows.add(row);
            cur.fetched++;
        }
        if (cur.fetched >= MAX_ROWS) cur.done = true;
        return rows;
    }

    public Map<String, Object> fetch(Map<String, Object> p) throws Exception {
        String queryId = Json.str(p.get("queryId"));
        int count = Json.intOf(p.get("count"), PAGE);
        Cursor cur = cursors.get(queryId);
        Map<String, Object> r = new LinkedHashMap<>();
        if (cur == null) {
            r.put("rows", new ArrayList<>());
            r.put("done", true);
            return r;
        }
        synchronized (connLock(cur.connId)) {
            List<List<String>> rows = readPage(cur, Math.max(1, Math.min(count, MAX_ROWS)));
            r.put("rows", rows);
            r.put("done", cur.done);
            if (cur.done) closeCursor(queryId);
            return r;
        }
    }

    public Map<String, Object> cancelConn(Map<String, Object> p) {
        String connId = Json.str(p.get("connId"));
        Statement st = activeStatements.get(connId);
        if (st != null) {
            try { st.cancel(); } catch (Exception ignored) { }
        }
        Map<String, Object> r = new LinkedHashMap<>();
        r.put("cancelled", st != null);
        return r;
    }

    public Map<String, Object> cancel(Map<String, Object> p) {
        String queryId = Json.str(p.get("queryId"));
        Cursor cur = cursors.get(queryId);
        if (cur != null) {
            try { cur.st.cancel(); } catch (Exception ignored) { }
        }
        Map<String, Object> r = new LinkedHashMap<>();
        r.put("cancelled", cur != null);
        return r;
    }

    public Map<String, Object> closeCursor(Map<String, Object> p) {
        closeCursor(Json.str(p.get("queryId")));
        Map<String, Object> r = new LinkedHashMap<>();
        r.put("closed", true);
        return r;
    }

    private void closeCursor(String queryId) {
        Cursor cur = cursors.remove(queryId);
        if (cur == null) return;
        try { cur.rs.close(); } catch (Exception ignored) { }
        try { cur.st.close(); } catch (Exception ignored) { }
        // Commit only the connection this cursor belongs to. Committing every
        // open connection here made a closing cursor wait for queries still
        // running on other connections (pg_sleep in one tab blocked the other).
        Conn own = conns.get(cur.connId);
        if (own != null) {
            try { own.c.commit(); } catch (Exception ignored) { }
        }
    }

    // ----------------------------------------------------------- value mapping

    private static String value(ResultSet rs, int idx, ResultSetMetaData md) throws SQLException {
        int type = md.getColumnType(idx);
        String typeName = md.getColumnTypeName(idx);
        String upper = typeName == null ? "" : typeName.toUpperCase(Locale.ROOT);

        if (type == Types.TIMESTAMP_WITH_TIMEZONE || upper.contains("WITH TIME ZONE")) {
            try {
                OffsetDateTime odt = rs.getObject(idx, OffsetDateTime.class);
                if (odt != null) return odt.format(DateTimeFormatter.ISO_OFFSET_DATE_TIME);
            } catch (Exception ignored) { }
        }

        Object o;
        try {
            o = rs.getObject(idx);
        } catch (SQLException e) {
            return "<" + e.getMessage() + ">";
        }
        if (o == null) return null;

        if (o instanceof Timestamp) return ((Timestamp) o).toLocalDateTime().format(DateTimeFormatter.ISO_LOCAL_DATE_TIME);
        if (o instanceof java.sql.Date) return ((java.sql.Date) o).toLocalDate().toString();
        if (o instanceof java.sql.Time) return ((java.sql.Time) o).toLocalTime().toString();
        if (o instanceof LocalDateTime) return ((LocalDateTime) o).format(DateTimeFormatter.ISO_LOCAL_DATE_TIME);
        if (o instanceof OffsetDateTime) return ((OffsetDateTime) o).format(DateTimeFormatter.ISO_OFFSET_DATE_TIME);
        if (o instanceof ZonedDateTime) return ((ZonedDateTime) o).format(DateTimeFormatter.ISO_OFFSET_DATE_TIME);
        if (o instanceof LocalDate) return o.toString();
        if (o instanceof LocalTime) return o.toString();
        if (o instanceof BigDecimal) return ((BigDecimal) o).toPlainString();
        if (o instanceof byte[]) return Base64.getEncoder().encodeToString((byte[]) o);
        if (o instanceof Boolean) return ((Boolean) o) ? "true" : "false";
        if (o instanceof Clob) {
            Clob clob = (Clob) o;
            long len = clob.length();
            int take = (int) Math.min(len, 1_000_000);
            return clob.getSubString(1, take) + (len > take ? "…" : "");
        }
        if (o instanceof Blob) {
            Blob blob = (Blob) o;
            int take = (int) Math.min(blob.length(), 1_000_000);
            return Base64.getEncoder().encodeToString(blob.getBytes(1, take));
        }
        if (o instanceof java.sql.Array) {
            try { return Arrays.deepToString((Object[]) ((java.sql.Array) o).getArray()); }
            catch (Exception ignored) { }
        }
        return String.valueOf(o);
    }

    // ------------------------------------------------------- editing results

    /** Ordered primary-key column names for one table. */
    public Map<String, Object> primaryKeys(Map<String, Object> p) throws Exception {
        String connId = Json.str(p.get("connId"));
        synchronized (connLock(connId)) {
            return primaryKeysLocked(p);
        }
    }

    private Map<String, Object> primaryKeysLocked(Map<String, Object> p) throws Exception {
        String connId = Json.str(p.get("connId"));
        Connection c = conn(connId);
        String catalog = emptyToNull(Json.str(p.get("catalog")));
        String schema = emptyToNull(Json.str(p.get("schema")));
        String table = Json.str(p.get("table"));
        List<String[]> keys = new ArrayList<>();
        try (ResultSet rs = c.getMetaData().getPrimaryKeys(catalog, schema, table)) {
            while (rs.next()) {
                keys.add(new String[]{
                        String.valueOf(rs.getShort("KEY_SEQ")),
                        rs.getString("COLUMN_NAME")
                });
            }
        }
        keys.sort(Comparator.comparingInt(a -> Integer.parseInt(a[0])));
        List<String> names = new ArrayList<>();
        for (String[] key : keys) names.add(key[1]);
        Map<String, Object> r = new LinkedHashMap<>();
        r.put("columns", names);
        return r;
    }

    /**
     * Column metadata for the header tooltip: type, size, nullability and the
     * primary key, in DBeaver's format.
     */
    public Map<String, Object> tableMeta(Map<String, Object> p) throws Exception {
        String connId = Json.str(p.get("connId"));
        synchronized (connLock(connId)) {
            return tableMetaLocked(p);
        }
    }

    private Map<String, Object> tableMetaLocked(Map<String, Object> p) throws Exception {
        String connId = Json.str(p.get("connId"));
        Connection c = conn(connId);
        String catalog = emptyToNull(Json.str(p.get("catalog")));
        String schema = emptyToNull(Json.str(p.get("schema")));
        String table = Json.str(p.get("table"));

        List<String> key = new ArrayList<>();
        String keyName = null;
        List<String[]> keys = new ArrayList<>();
        try (ResultSet rs = c.getMetaData().getPrimaryKeys(catalog, schema, table)) {
            while (rs.next()) {
                keys.add(new String[]{
                        String.valueOf(rs.getShort("KEY_SEQ")),
                        rs.getString("COLUMN_NAME"),
                        rs.getString("PK_NAME")
                });
            }
        }
        keys.sort(Comparator.comparingInt(a -> Integer.parseInt(a[0])));
        for (String[] k : keys) {
            key.add(k[1]);
            if (keyName == null && k[2] != null && !k[2].isEmpty()) keyName = k[2];
        }

        List<Map<String, Object>> columns = new ArrayList<>();
        try (ResultSet rs = c.getMetaData().getColumns(catalog, schema, table, null)) {
            while (rs.next()) {
                String name = rs.getString("COLUMN_NAME");
                Map<String, Object> col = new LinkedHashMap<>();
                col.put("name", name);
                col.put("type", rs.getString("TYPE_NAME"));
                int size = rs.getInt("COLUMN_SIZE");
                if (!rs.wasNull() && size > 0) col.put("size", size);
                col.put("nullable", rs.getInt("NULLABLE") != DatabaseMetaData.columnNoNulls);
                col.put("primary", key.contains(name));
                columns.add(col);
            }
        }

        Map<String, Object> r = new LinkedHashMap<>();
        r.put("columns", columns);
        r.put("primaryKey", key);
        if (keyName != null) r.put("primaryKeyName", keyName);
        return r;
    }

    /**
     * Applies cell edits as UPDATE statements. A short-lived second connection
     * is used so an open result cursor on the main connection is not disturbed.
     * Each update carries the original primary-key values and the new values.
     */
    public Map<String, Object> updateRows(Map<String, Object> p) throws Exception {
        String connId = Json.str(p.get("connId"));
        Conn base = conns.get(connId);
        if (base == null) throw new SQLException("Нет соединения");

        String catalog = Json.str(p.get("catalog"));
        String schema = Json.str(p.get("schema"));
        String table = Json.str(p.get("table"));
        List<Object> updates = Json.arr(p.get("updates"));
        List<Object> inserts = Json.arr(p.get("inserts"));
        if (updates.isEmpty() && inserts.isEmpty()) {
            Map<String, Object> empty = new LinkedHashMap<>();
            empty.put("updated", 0);
            empty.put("inserted", 0);
            return empty;
        }

        Connection c = DriverManager.getConnection(base.url, base.props);
        boolean oldAutoCommit = c.getAutoCommit();
        int updated = 0;
        int inserted = 0;
        try {
            c.setAutoCommit(false);
            String qualified = qualifiedName(c, catalog, schema, table);
            Map<String, Integer> types = columnTypes(c, qualified);
            String quote = identifierQuote(c);

            for (Object raw : updates) {
                Map<String, Object> update = Json.obj(raw);
                Map<String, Object> values = Json.obj(update.get("values"));
                Map<String, Object> keys = Json.obj(update.get("keys"));
                if (values.isEmpty()) continue;
                if (keys.isEmpty()) throw new SQLException("Нет первичного ключа для строки");

                StringBuilder sql = new StringBuilder("UPDATE ").append(qualified).append(" SET ");
                boolean first = true;
                for (String column : values.keySet()) {
                    if (!first) sql.append(", ");
                    first = false;
                    sql.append(quoteIdentifier(column, quote)).append(" = ?");
                }
                sql.append(" WHERE ");
                first = true;
                for (String column : keys.keySet()) {
                    if (!first) sql.append(" AND ");
                    first = false;
                    Object value = keys.get(column);
                    sql.append(quoteIdentifier(column, quote));
                    sql.append(value == null ? " IS NULL" : " = ?");
                }

                try (PreparedStatement ps = c.prepareStatement(sql.toString())) {
                    int index = 1;
                    for (Map.Entry<String, Object> e : values.entrySet()) {
                        bind(ps, index++, e.getValue(), types.get(e.getKey().toLowerCase(Locale.ROOT)));
                    }
                    for (Map.Entry<String, Object> e : keys.entrySet()) {
                        if (e.getValue() == null) continue;
                        bind(ps, index++, e.getValue(), types.get(e.getKey().toLowerCase(Locale.ROOT)));
                    }
                    updated += ps.executeUpdate();
                }
            }

            for (Object raw : inserts) {
                Map<String, Object> values = Json.obj(raw);
                if (values.isEmpty()) continue;
                StringBuilder sql = new StringBuilder("INSERT INTO ")
                        .append(qualified).append(" (");
                boolean first = true;
                for (String column : values.keySet()) {
                    if (!first) sql.append(", ");
                    first = false;
                    sql.append(quoteIdentifier(column, quote));
                }
                sql.append(") VALUES (");
                first = true;
                for (int i = 0; i < values.size(); i++) {
                    if (!first) sql.append(", ");
                    first = false;
                    sql.append('?');
                }
                sql.append(')');

                try (PreparedStatement ps = c.prepareStatement(sql.toString())) {
                    int index = 1;
                    for (Map.Entry<String, Object> e : values.entrySet()) {
                        bind(ps, index++, e.getValue(), types.get(e.getKey().toLowerCase(Locale.ROOT)));
                    }
                    inserted += ps.executeUpdate();
                }
            }
            c.commit();
        } catch (Exception e) {
            try { c.rollback(); } catch (Exception ignored) { }
            throw e;
        } finally {
            try { c.setAutoCommit(oldAutoCommit); } catch (Exception ignored) { }
            try { c.close(); } catch (Exception ignored) { }
        }

        Map<String, Object> r = new LinkedHashMap<>();
        r.put("updated", updated);
        r.put("inserted", inserted);
        return r;
    }

    private static String emptyToNull(String value) {
        return value == null || value.isEmpty() ? null : value;
    }

    private static String identifierQuote(Connection c) {
        try {
            String quote = c.getMetaData().getIdentifierQuoteString();
            return quote == null || quote.trim().isEmpty() ? "" : quote;
        } catch (Exception ignored) {
            return "";
        }
    }

    private static String quoteIdentifier(String name, String quote) {
        if (quote.isEmpty()) return name;
        return quote + name.replace(quote, quote + quote) + quote;
    }

    /** schema.table, or catalog.table when there is no schema (MySQL). */
    private static String qualifiedName(Connection c, String catalog, String schema, String table) {
        String quote = identifierQuote(c);
        StringBuilder name = new StringBuilder();
        if (schema != null && !schema.isEmpty()) {
            name.append(quoteIdentifier(schema, quote)).append('.');
        } else if (catalog != null && !catalog.isEmpty()) {
            name.append(quoteIdentifier(catalog, quote)).append('.');
        }
        name.append(quoteIdentifier(table, quote));
        return name.toString();
    }

    /** JDBC type of every column, so edited strings are bound with the right type. */
    private static Map<String, Integer> columnTypes(Connection c, String qualified) throws SQLException {
        Map<String, Integer> types = new HashMap<>();
        try (Statement st = c.createStatement();
             ResultSet rs = st.executeQuery("SELECT * FROM " + qualified + " WHERE 1=0")) {
            ResultSetMetaData md = rs.getMetaData();
            for (int i = 1; i <= md.getColumnCount(); i++) {
                types.put(md.getColumnLabel(i).toLowerCase(Locale.ROOT), md.getColumnType(i));
                types.put(md.getColumnName(i).toLowerCase(Locale.ROOT), md.getColumnType(i));
            }
        }
        return types;
    }

    private static void bind(PreparedStatement ps, int index, Object raw, Integer jdbcType) throws SQLException {
        int type = jdbcType == null ? Types.VARCHAR : jdbcType;
        if (raw == null) {
            ps.setNull(index, type == Types.NULL ? Types.VARCHAR : type);
            return;
        }
        String value = String.valueOf(raw);
        switch (type) {
            case Types.BOOLEAN:
            case Types.BIT:
                ps.setBoolean(index, booleanValue(value));
                break;
            case Types.TINYINT:
            case Types.SMALLINT:
            case Types.INTEGER:
            case Types.BIGINT:
                ps.setLong(index, Long.parseLong(value.trim()));
                break;
            case Types.NUMERIC:
            case Types.DECIMAL:
                ps.setBigDecimal(index, new BigDecimal(value.trim()));
                break;
            case Types.REAL:
            case Types.FLOAT:
            case Types.DOUBLE:
                ps.setDouble(index, Double.parseDouble(value.trim()));
                break;
            case Types.DATE:
                ps.setDate(index, java.sql.Date.valueOf(value.length() >= 10 ? value.substring(0, 10) : value));
                break;
            case Types.TIME:
                ps.setTime(index, java.sql.Time.valueOf(value));
                break;
            case Types.TIMESTAMP:
                ps.setTimestamp(index, Timestamp.valueOf(value.replace('T', ' ').replace("Z", "")));
                break;
            case Types.TIMESTAMP_WITH_TIMEZONE:
                try {
                    ps.setObject(index, OffsetDateTime.parse(value));
                } catch (Exception e) {
                    ps.setObject(index, value, type);
                }
                break;
            case Types.BINARY:
            case Types.VARBINARY:
            case Types.LONGVARBINARY:
            case Types.BLOB:
                ps.setBytes(index, Base64.getDecoder().decode(value));
                break;
            default:
                ps.setString(index, value);
        }
    }

    private static boolean booleanValue(String value) {
        String v = value.trim().toLowerCase(Locale.ROOT);
        return v.equals("true") || v.equals("t") || v.equals("1")
                || v.equals("yes") || v.equals("y");
    }

    public void shutdown() {
        for (String id : new ArrayList<>(cursors.keySet())) closeCursor(id);
        for (String id : new ArrayList<>(conns.keySet())) {
            try { conns.remove(id).c.close(); } catch (Exception ignored) { }
        }
    }
}
