<?php
/**
 * Plugin Name: Keiba Race Sync
 * Description: JV-Link/UmaConn連携の常駐アプリ（KeibaDataCollector）から送られる出走表・結果データを受け取り、
 *              カスタム投稿タイプ「race」として保存・表示する。
 * Version: 0.1.6
 */

if (!defined('ABSPATH')) {
    exit;
}

define('KEIBA_RACE_SYNC_JSON_META_KEYS', array('race_card', 'race_result', 'payouts', 'corner_passage'));

// 稼働中のバージョン確認用（/wp-json/keiba-race-sync/v1/health で参照）。
define('KEIBA_RACE_SYNC_VERSION', '0.1.6');

// CSS/JS のキャッシュ更新用。アセットを変更したらここを上げる。
define('KEIBA_RACE_SYNC_ASSET_VER', '0.2.2');

/**
 * カスタム投稿タイプ「race」を登録。
 * 表示内容はエディタで書くのではなく the_content フィルタで自動生成するため editor はサポートしない。
 */
add_action('init', function () {
    register_post_type('race', array(
        'label' => 'レース',
        'labels' => array(
            'name' => 'レース',
            'singular_name' => 'レース',
        ),
        'public' => true,
        'show_in_rest' => true,
        'rest_base' => 'race',
        // 'custom-fields' が supports に無いと、register_post_meta で登録したメタが
        // REST レスポンスの "meta" プロパティに一切出てこない（WPの既知の挙動）。
        'supports' => array('title', 'custom-fields'),
        'has_archive' => true,
        'rewrite' => array('slug' => 'race'),
        'menu_icon' => 'dashicons-flag',
    ));
});

/**
 * KeibaDataCollector（C#常駐アプリ）が送るメタキーをREST経由で読み書き可能にする。
 * race_card / race_result / payouts / corner_passage は camelCase キーのJSON文字列として
 * 保存する取り決め（WordPressClient.cs 側もこの形式で送信する）。
 */
add_action('init', function () {
    foreach (KEIBA_RACE_SYNC_JSON_META_KEYS as $meta_key) {
        register_post_meta('race', $meta_key, array(
            'type' => 'string',
            'single' => true,
            'show_in_rest' => true,
            'sanitize_callback' => 'keiba_race_sync_sanitize_json_meta',
            'auth_callback' => function () {
                return current_user_can('edit_posts');
            },
        ));
    }

    // 予想印（馬番 => 印）。データ提供元には無く、サイト側で入力する項目のため
    // 収集アプリ側は一切送信しない＝自動更新で消えることはない。
    register_post_meta('race', 'predictions', array(
        'type' => 'string',
        'single' => true,
        'show_in_rest' => true,
        'sanitize_callback' => 'keiba_race_sync_sanitize_json_meta',
        'auth_callback' => function () {
            return current_user_can('edit_posts');
        },
    ));

    register_post_meta('race', 'race_key', array(
        'type' => 'string',
        'single' => true,
        'show_in_rest' => true,
        'sanitize_callback' => 'sanitize_text_field',
        'auth_callback' => function () {
            return current_user_can('edit_posts');
        },
    ));
});

/* ------------------------------------------------------------------------- *
 * ページキャッシュの破棄
 *
 * 結果を書き込んでもページキャッシュが残っていると、訪問者には朝に生成された
 * 「出走表だけ」のHTMLが配信され続ける。データは正しいのにサイトには出ない。
 *
 * 実際に発生した障害（2026-08-05）:
 *   投稿は17:22に結果込みで更新されていたが、WP Rocketが08:22〜10:17に生成した
 *   HTMLを返し続けていた。キャッシュが無かったレースだけが着順を表示していたため、
 *   「一部のレースだけ更新される」という分かりにくい形でお客様に見えていた。
 *
 * キャッシュプラグインは投稿の更新時に自動破棄する作りだが、収集アプリはRESTで
 * メタだけを更新するため、その破棄が働かないことがある。ここで明示的に破棄する。
 * ------------------------------------------------------------------------- */

/**
 * 1リクエスト内で同じ投稿を何度も破棄しないよう溜めてからまとめて処理する。
 * 1レースあたりメタを4〜5個更新するため、素直に書くと同じ破棄を5回呼ぶことになる。
 */
function keiba_race_sync_queue_purge($post_id)
{
    static $hooked = false;
    static $queue = array();

    $post_id = (int) $post_id;
    if ($post_id <= 0 || isset($queue[$post_id])) {
        return;
    }
    $queue[$post_id] = true;

    if (!$hooked) {
        $hooked = true;
        add_action('shutdown', function () use (&$queue) {
            $deleted = 0;
            foreach (array_keys($queue) as $id) {
                $deleted += (int) keiba_race_sync_purge_post_cache($id);
            }
            $deleted += (int) keiba_race_sync_purge_listing_pages();

            // 破棄が本当に行われたかを外から確認できるように記録する。
            // これが無かったため、キャッシュが消えない原因の切り分けに時間を要した。
            update_option('keiba_race_sync_last_purge', array(
                'time' => current_time('mysql'),
                'posts' => count($queue),
                'files' => $deleted,
            ), false);
        }, 100);
    }
}

foreach (array('updated_post_meta', 'added_post_meta') as $keiba_meta_hook) {
    add_action($keiba_meta_hook, function ($meta_id, $object_id, $meta_key) {
        $watched = array_merge(KEIBA_RACE_SYNC_JSON_META_KEYS, array('predictions'));
        if (!in_array($meta_key, $watched, true)) {
            return;
        }
        if (get_post_type($object_id) !== 'race') {
            return;
        }
        keiba_race_sync_queue_purge($object_id);
    }, 10, 3);
}
unset($keiba_meta_hook);

/**
 * 個別レースページのキャッシュを破棄する。
 * どのキャッシュプラグインが入っているか環境側の都合で変わりうるので、
 * 主要なものを「あれば呼ぶ」形で並べている。無ければ何も起きない。
 */
function keiba_race_sync_purge_post_cache($post_id)
{
    if (function_exists('rocket_clean_post')) {          // WP Rocket
        rocket_clean_post($post_id);
    }
    if (function_exists('wp_cache_post_change')) {       // WP Super Cache
        wp_cache_post_change($post_id);
    }
    if (function_exists('w3tc_flush_post')) {            // W3 Total Cache
        w3tc_flush_post($post_id);
    }
    if (function_exists('wpfc_clear_post_cache_by_id')) { // WP Fastest Cache
        wpfc_clear_post_cache_by_id($post_id);
    }
    do_action('litespeed_purge_post', $post_id);          // LiteSpeed Cache
    do_action('cache_enabler_clear_page_cache_by_post', $post_id);

    // 上のAPIが効かなかった場合の保険。実測で、rocket_clean_post を呼んでも
    // キャッシュファイルが残り続ける事象が発生したため、ファイルを直接削除する。
    $deleted = keiba_race_sync_delete_rocket_files($post_id);

    // サーバー側キャッシュ等、ここで拾えない仕組み向けの拡張点。
    do_action('keiba_race_sync_purge_post', $post_id);

    return $deleted;
}

/**
 * WP Rocket が書き出したページキャッシュのファイルを直接削除する。
 *
 * rocket_clean_post() を呼んでも消えないことが実機で確認されたため、
 * プラグインのAPIに頼らず、キャッシュの実体を消す経路も用意する。
 * 保存場所は wp-content/cache/wp-rocket/<ホスト名>/<パス>/ で、
 * その配下に index.html / index-https.html などが置かれる。
 *
 * 消してよいのはキャッシュ配下だけなので、
 * 実パスがキャッシュルート配下であることを必ず確かめてから削除する。
 */
function keiba_race_sync_delete_rocket_files($post_id)
{
    if (!defined('WP_CONTENT_DIR')) {
        return 0;
    }

    $root = realpath(WP_CONTENT_DIR . '/cache/wp-rocket');
    if ($root === false) {
        return 0; // WP Rocket が無い環境。
    }

    $permalink = get_permalink($post_id);
    if (!$permalink) {
        return 0;
    }
    $parts = wp_parse_url($permalink);
    if (empty($parts['host'])) {
        return 0;
    }
    $path = isset($parts['path']) ? $parts['path'] : '/';

    // パーマリンクは日本語スラッグでURLエンコードされるが、
    // WP Rocket はエンコードされたままのディレクトリ名で保存する。
    $dir = realpath($root . '/' . $parts['host'] . $path);
    if ($dir === false) {
        return 0; // まだキャッシュされていない。
    }

    // キャッシュルートの外を消してしまわないための保険。
    // ルート直下そのもの（トップページを一覧に使っている場合）は許可する。
    if ($dir !== $root && strpos($dir, $root . DIRECTORY_SEPARATOR) !== 0) {
        return 0;
    }

    $deleted = 0;
    foreach ((array) glob($dir . '/index*.html*') as $file) {
        if (is_file($file) && @unlink($file)) {
            $deleted++;
        }
    }
    return $deleted;
}

/**
 * プラグインを更新したら、サイト全体のページキャッシュを一度だけ破棄する。
 *
 * 用意した理由:
 *   このプラグインはFTPやFile Managerでファイルを直接上書きして更新している。
 *   その方法ではWordPressの更新フックが一切走らないため、キャッシュプラグインは
 *   更新に気付けず、古いHTMLを配信し続ける。
 *   実際に、競馬場名の修正を入れたのに一覧ページが「83」のままになる事象が発生した
 *   （表示ロジックは直っていたが、キャッシュが修正前のHTMLを返していた）。
 *   毎回手作業でキャッシュクリアするのは忘れるので、自分で気付くようにする。
 *
 * バージョン定数の変化で判定するため、更新のたびに自動で1回だけ走る。
 */
add_action('init', function () {
    if (get_option('keiba_race_sync_installed_version') === KEIBA_RACE_SYNC_VERSION) {
        return;
    }
    // 先に記録する。破棄処理が落ちても毎リクエスト走り続けないようにするため。
    update_option('keiba_race_sync_installed_version', KEIBA_RACE_SYNC_VERSION, true);
    keiba_race_sync_purge_everything();
}, 99);

/**
 * サイト全体のキャッシュを破棄する。プラグイン更新時のみ呼ぶ。
 * 表示の作りが変わったときは、どのページが影響を受けるか特定できないため全体を対象にする。
 */
function keiba_race_sync_purge_everything()
{
    if (function_exists('rocket_clean_domain')) {        // WP Rocket
        rocket_clean_domain();
    }
    if (function_exists('wp_cache_clear_cache')) {       // WP Super Cache
        wp_cache_clear_cache();
    }
    if (function_exists('w3tc_flush_all')) {             // W3 Total Cache
        w3tc_flush_all();
    }
    if (function_exists('wpfc_clear_all_cache')) {       // WP Fastest Cache
        wpfc_clear_all_cache(true);
    }
    do_action('litespeed_purge_all');
    do_action('cache_enabler_clear_complete_cache');

    // 一覧ページだけは確実に消す。お客様が最初にご覧になる画面のため。
    keiba_race_sync_purge_listing_pages();
}

/**
 * レース選択UIを置いたページのキャッシュも破棄する。
 * お客様が実際にご覧になるのは個別ページより一覧ページ（/today-races/ など）のため、
 * ここが古いままだと「結果が出ていない」と見える。
 *
 * 対象ページはショートコードの有無で判定し、毎回の全件走査を避けるため一時保存する。
 */
function keiba_race_sync_listing_page_ids()
{
    $ids = get_transient('keiba_race_sync_listing_pages');
    if (is_array($ids)) {
        return $ids;
    }

    $ids = array();
    $pages = get_posts(array(
        'post_type' => array('page', 'post'),
        'post_status' => 'publish',
        'posts_per_page' => 200,
        'no_found_rows' => true,
        'suppress_filters' => true,
    ));
    foreach ($pages as $page) {
        if (has_shortcode($page->post_content, 'keiba_race_selector')) {
            $ids[] = (int) $page->ID;
        }
    }

    set_transient('keiba_race_sync_listing_pages', $ids, HOUR_IN_SECONDS);
    return $ids;
}

function keiba_race_sync_purge_listing_pages()
{
    $deleted = 0;
    foreach (keiba_race_sync_listing_page_ids() as $id) {
        $deleted += (int) keiba_race_sync_purge_post_cache($id);
    }
    return $deleted;
}

/**
 * 稼働中のバージョンとキャッシュ破棄の実績を外から確認するためのエンドポイント。
 *
 *   GET /wp-json/keiba-race-sync/v1/health
 *
 * 用意した理由: サーバー上のPHPはHTTPからは読めないため、
 * 「どのバージョンが動いているのか」を推測するしかなく、
 * 不具合の切り分けが遅れた。バージョンと最終破棄時刻が見えれば一目で分かる。
 * 公開情報のみを返す（投稿内容・設定・認証情報は含めない）。
 */
add_action('rest_api_init', function () {
    register_rest_route('keiba-race-sync/v1', '/health', array(
        'methods' => 'GET',
        'permission_callback' => '__return_true',
        'callback' => function () {
            return array(
                'version' => KEIBA_RACE_SYNC_VERSION,
                'assetVersion' => KEIBA_RACE_SYNC_ASSET_VER,
                'lastPurge' => get_option('keiba_race_sync_last_purge', null),
                // 対応表に無い競馬場コード。空でなければ名称の追加が必要。
                'unknownTracks' => get_option('keiba_race_sync_unknown_tracks', new stdClass()),
                'rocketCacheRoot' => (realpath(WP_CONTENT_DIR . '/cache/wp-rocket') !== false),
                'purgeApis' => array(
                    'rocket' => function_exists('rocket_clean_post'),
                    'superCache' => function_exists('wp_cache_post_change'),
                    'w3tc' => function_exists('w3tc_flush_post'),
                    'fastest' => function_exists('wpfc_clear_post_cache_by_id'),
                ),
            );
        },
    ));
});

// ページを編集したら、ショートコードの有無が変わっている可能性があるので拾い直す。
add_action('save_post', function ($post_id, $post) {
    if ($post instanceof WP_Post && in_array($post->post_type, array('page', 'post'), true)) {
        delete_transient('keiba_race_sync_listing_pages');
    }
}, 10, 2);

/**
 * JSON文字列メタのサニタイズ。不正なJSONは空配列にフォールバックし、壊れた表示を防ぐ。
 */
function keiba_race_sync_sanitize_json_meta($value)
{
    if (is_array($value) || is_object($value)) {
        // 稀にWPがリクエストJSONを配列として渡してくる場合はそのまま文字列化する。
        return wp_json_encode($value);
    }

    $value = (string) $value;
    json_decode($value);
    return json_last_error() === JSON_ERROR_NONE ? $value : '[]';
}

/**
 * WordPressClient.cs の FindPostIdByRaceKeyAsync が使う
 * GET /wp-json/wp/v2/race?meta_key=race_key&meta_value=xxxx を有効にする。
 * 任意メタキーでの検索を許すとメタの総当たり探索を許してしまうため race_key のみ許可する。
 */
add_filter('rest_race_query', function ($args, $request) {
    if ($request->get_param('meta_key') === 'race_key') {
        $meta_value = $request->get_param('meta_value');
        if ($meta_value !== null && $meta_value !== '') {
            $args['meta_query'] = array(
                array(
                    'key' => 'race_key',
                    'value' => sanitize_text_field($meta_value),
                ),
            );
        }
    }
    return $args;
}, 10, 2);

add_action('wp_enqueue_scripts', function () {
    if (is_singular('race')) {
        wp_enqueue_style(
            'keiba-race-sync',
            plugins_url('assets/keiba-race-sync.css', __FILE__),
            array(),
            KEIBA_RACE_SYNC_ASSET_VER
        );
        return;
    }

    // レース選択UIを置いたページ。
    // ショートコード内で wp_enqueue_style を呼ぶだけだと、the_content は wp_head より
    // 後に実行されるため読み込みが遅れる（キャッシュ/最適化プラグインとの相性も悪い）。
    // ここで本文を先に調べて、通常のタイミングで読み込ませる。
    if (is_singular()) {
        $post = get_post();
        if ($post && has_shortcode($post->post_content, 'keiba_race_selector')) {
            keiba_race_sync_enqueue_selector_assets();
        }
    }
});

/**
 * レース個別ページの本文を自動生成する。テーマのheader/footerはそのまま使う。
 */
add_filter('the_content', function ($content) {
    if (!is_singular('race') || !in_the_loop() || !is_main_query()) {
        return $content;
    }
    return $content . keiba_race_sync_render_race(get_the_ID());
});

function keiba_race_sync_decode_meta($post_id, $meta_key)
{
    $raw = get_post_meta($post_id, $meta_key, true);
    if (empty($raw)) {
        return array();
    }
    $decoded = json_decode($raw, true);
    return is_array($decoded) ? $decoded : array();
}

function keiba_race_sync_render_race($post_id)
{
    $race_card = keiba_race_sync_decode_meta($post_id, 'race_card');
    $race_result = keiba_race_sync_decode_meta($post_id, 'race_result');
    $payouts = keiba_race_sync_decode_meta($post_id, 'payouts');
    $corner_passage = keiba_race_sync_decode_meta($post_id, 'corner_passage');

    // 予想印はデータ提供元（JV-Link/UmaConn）には無く、サイト側で入力するもの。
    // 入力があるレースだけ「予想」列を出す。
    $predictions = keiba_race_sync_decode_meta($post_id, 'predictions');

    ob_start();
    echo '<div class="keiba-race">';

    if (!empty($race_result)) {
        keiba_race_sync_render_result_table($race_result, $predictions);
    } elseif (!empty($race_card)) {
        keiba_race_sync_render_card_table($race_card, $predictions);
    }

    if (!empty($payouts) || !empty($corner_passage)) {
        echo '<div class="keiba-sub-tables">';
        if (!empty($payouts)) {
            keiba_race_sync_render_payout_table($payouts);
        }
        if (!empty($corner_passage)) {
            keiba_race_sync_render_corner_table($corner_passage);
        }
        echo '</div>';
    }

    echo '</div>';
    return ob_get_clean();
}

function keiba_race_sync_waku_badge($waku)
{
    $waku = (int) $waku;
    if ($waku < 1 || $waku > 8) {
        return '';
    }
    return sprintf('<span class="keiba-waku keiba-waku-%d">%d</span>', $waku, $waku);
}

function keiba_race_sync_render_card_table($entries, $predictions = array())
{
    $show_prediction = !empty($predictions);

    // 列数が多く、馬名も長くなりうるため、狭い画面では折り返さず横スクロールさせる。
    echo '<div class="keiba-table-scroll">';
    echo '<table class="keiba-table keiba-card-table">';
    echo '<thead><tr>'
        . '<th>枠</th><th>馬番</th><th>馬名</th><th>性齢</th><th>斤量</th><th>騎手</th><th>厩舎</th>';
    if ($show_prediction) {
        echo '<th>予想</th>';
    }
    echo '</tr></thead><tbody>';
    foreach ($entries as $e) {
        echo '<tr>';
        echo '<td>' . keiba_race_sync_waku_badge($e['waku'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['umaban'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['horseName'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['sexAge'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['kinryo'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['jockeyName'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['trainerName'] ?? '') . '</td>';
        if ($show_prediction) {
            echo '<td class="keiba-yosou">' . esc_html(keiba_race_sync_prediction_for($predictions, $e['umaban'] ?? null)) . '</td>';
        }
        echo '</tr>';
    }
    echo '</tbody></table>';
    echo '</div>';
}

/**
 * 予想印は「馬番 => 印」の連想配列。JSONのキーは文字列になるため両方の型を見る。
 */
function keiba_race_sync_prediction_for($predictions, $umaban)
{
    if ($umaban === null) {
        return '';
    }
    if (isset($predictions[(string) $umaban])) {
        return $predictions[(string) $umaban];
    }
    if (isset($predictions[(int) $umaban])) {
        return $predictions[(int) $umaban];
    }
    return '';
}

/**
 * 着順テーブル用に並べ替える。
 *
 * データ提供元（JV-Link/UmaConn）は成績レコードを馬番順で返すため、そのまま出すと
 * 「着順」列が 12着→7着→9着… と並び、着順表として読めない。
 * 取消・除外・競走中止は着順が付かない（0）ので、末尾に馬番順でまとめる。
 *
 * 保存済みのデータには手を加えず表示時に並べ替えるので、
 * 公開済みのレースもこの修正だけで正しい並びになる。
 */
function keiba_race_sync_sort_by_finish($entries)
{
    usort($entries, function ($a, $b) {
        $ca = isset($a['chakujun']) ? (int) $a['chakujun'] : 0;
        $cb = isset($b['chakujun']) ? (int) $b['chakujun'] : 0;

        // 着順なし(0)は必ず後ろ。両方0なら馬番順。
        if ($ca === 0 || $cb === 0) {
            if ($ca !== $cb) {
                return $ca === 0 ? 1 : -1;
            }
            return ((int) ($a['umaban'] ?? 0)) - ((int) ($b['umaban'] ?? 0));
        }
        if ($ca !== $cb) {
            return $ca - $cb;
        }
        // 同着は馬番順で安定させる。
        return ((int) ($a['umaban'] ?? 0)) - ((int) ($b['umaban'] ?? 0));
    });

    return $entries;
}

function keiba_race_sync_render_result_table($entries, $predictions = array())
{
    $entries = keiba_race_sync_sort_by_finish($entries);
    $show_prediction = !empty($predictions);

    // 14列あるため、狭い画面では折り返さず横スクロールさせる。
    echo '<div class="keiba-table-scroll">';
    echo '<table class="keiba-table keiba-result-table">';
    echo '<thead><tr>'
        . '<th>着順</th><th>枠</th><th>馬番</th><th>馬名</th><th>性齢</th><th>斤量</th><th>騎手</th>'
        . '<th>タイム</th><th>着差</th><th>人気</th><th>単勝</th><th>後3F</th><th>厩舎</th><th>馬体重(増減)</th>';
    if ($show_prediction) {
        echo '<th>予想</th>';
    }
    echo '</tr></thead><tbody>';
    foreach ($entries as $e) {
        $bataiju = esc_html($e['bataijuuZengo'] ?? '');
        $zogen = isset($e['bataijuuZogen']) ? (int) $e['bataijuuZogen'] : 0;
        $zogen_text = $zogen > 0 ? "(+{$zogen})" : ($zogen < 0 ? "({$zogen})" : '(0)');

        // 取消・除外・競走中止は着順が付かず0で入ってくる。そのまま出すと
        // 「0着」という存在しない着順に見えるため、印字しない。
        $chakujun = isset($e['chakujun']) ? (int) $e['chakujun'] : 0;
        $chakujun_text = $chakujun > 0 ? (string) $chakujun : '－';

        echo '<tr' . ($chakujun > 0 ? '' : ' class="keiba-no-finish"') . '>';
        echo '<td>' . esc_html($chakujun_text) . '</td>';
        echo '<td>' . keiba_race_sync_waku_badge($e['waku'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['umaban'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['horseName'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['sexAge'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['kinryo'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['jockeyName'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['time'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['chakusaText'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['ninki'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['tanshoOdds'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['ushi3F'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['trainerName'] ?? '') . '</td>';
        echo '<td>' . $bataiju . $zogen_text . '</td>';
        if ($show_prediction) {
            echo '<td class="keiba-yosou">' . esc_html(keiba_race_sync_prediction_for($predictions, $e['umaban'] ?? null)) . '</td>';
        }
        echo '</tr>';
    }
    echo '</tbody></table>';
    echo '</div>';
}

function keiba_race_sync_render_payout_table($payouts)
{
    // 組み合わせの人気順は、地方競馬DATAでは配信されず常に0になる（実データで確認）。
    // 全件0のまま「0人気」と出すと誤情報に見えるため、その場合は列ごと出さない。
    $has_ninki = false;
    foreach ($payouts as $p) {
        if ((int) ($p['ninki'] ?? 0) > 0) {
            $has_ninki = true;
            break;
        }
    }

    echo '<table class="keiba-table keiba-payout-table">';
    echo '<caption>払戻金</caption>';
    echo '<thead><tr><th>券種</th><th>組み合わせ</th><th>金額</th>';
    if ($has_ninki) {
        echo '<th>人気</th>';
    }
    echo '</tr></thead><tbody>';
    foreach ($payouts as $p) {
        echo '<tr>';
        echo '<td>' . esc_html($p['ticketType'] ?? '') . '</td>';
        echo '<td>' . esc_html($p['combination'] ?? '') . '</td>';
        echo '<td>' . esc_html(number_format((float) ($p['amount'] ?? 0))) . '円</td>';
        if ($has_ninki) {
            $ninki = (int) ($p['ninki'] ?? 0);
            echo '<td>' . ($ninki > 0 ? esc_html($ninki) . '人気' : '&mdash;') . '</td>';
        }
        echo '</tr>';
    }
    echo '</tbody></table>';
}

function keiba_race_sync_render_corner_table($corners)
{
    echo '<table class="keiba-table keiba-corner-table">';
    echo '<caption>コーナー通過順位</caption>';
    echo '<tbody>';
    foreach ($corners as $i => $passage) {
        echo '<tr><th>' . ($i + 1) . 'コーナー</th><td>' . esc_html($passage) . '</td></tr>';
    }
    echo '</tbody></table>';
}

/* ==========================================================================
 * レース選択UI（競馬場を選ぶ → レース番号を選ぶ → 出走表を表示）
 * ========================================================================== */

/**
 * 競馬場コード → 表示名。
 * JV-Data仕様書「コード表 2001.競馬場コード」に基づく。
 * 地方競馬DATA(UmaConn)は概ね同じコード体系だが、この表に無いコードを返す場合がある
 * （実データで "83" を確認済み）。未知のコードは番号のまま表示して取りこぼさない。
 */
function keiba_race_sync_track_name($code)
{
    $code = trim((string) $code);

    static $names = array(
        '01' => '札幌', '02' => '函館', '03' => '福島', '04' => '新潟', '05' => '東京',
        '06' => '中山', '07' => '中京', '08' => '京都', '09' => '阪神', '10' => '小倉',
        '30' => '門別', '31' => '北見', '32' => '岩見沢', '33' => '帯広', '34' => '旭川',
        '35' => '盛岡', '36' => '水沢', '37' => '上山', '38' => '三条', '39' => '足利',
        '40' => '宇都宮', '41' => '高崎', '42' => '浦和', '43' => '船橋', '44' => '大井',
        '45' => '川崎', '46' => '金沢', '47' => '笠松', '48' => '名古屋', '49' => '紀三井寺',
        '50' => '園田', '51' => '姫路', '52' => '益田', '53' => '福山', '54' => '高知',
        '55' => '佐賀', '56' => '荒尾', '57' => '中津',
        '58' => '札幌(地方)', '59' => '函館(地方)', '60' => '新潟(地方)', '61' => '中京(地方)',

        // ばんえい帯広。コード33の「帯広」は平地時代のもので別物のため、区別できる名前にする。
        // 実データで確認: 1レース内の走破タイムが1:51〜2:26と35秒もばらつき、
        // 後3ハロンが全頭未提供、9〜10頭立て。いずれもばんえい競走の特徴。
        '83' => 'ばんえい帯広',
    );

    if (isset($names[$code])) {
        return $names[$code];
    }

    if ($code === '') {
        return '不明';
    }

    // 未知のコードをそのまま出すと、お客様には「83」という競馬場があるように見える
    // （実際にご指摘をいただいた）。コードだと分かる形にし、記録して気付けるようにする。
    keiba_race_sync_record_unknown_track($code);
    return '競馬場' . $code;
}

/**
 * 対応表に無い競馬場コードを記録する。
 * お客様に指摘されるまで気付けなかったため、health から確認できるようにしておく。
 */
function keiba_race_sync_record_unknown_track($code)
{
    $known = get_option('keiba_race_sync_unknown_tracks', array());
    if (!is_array($known)) {
        $known = array();
    }
    if (isset($known[$code])) {
        return;
    }
    $known[$code] = current_time('mysql');
    update_option('keiba_race_sync_unknown_tracks', $known, false);
}

/**
 * race_key（"20260803-35-1R"）を分解する。想定外の形式なら null。
 */
function keiba_race_sync_parse_race_key($race_key)
{
    if (!preg_match('/^(\d{8})-(\w+)-(\d+)R$/', (string) $race_key, $m)) {
        return null;
    }
    return array(
        'date'   => $m[1],
        'track'  => $m[2],
        'number' => (int) $m[3],
    );
}

/**
 * 指定日のレース投稿を「競馬場 → レース番号」でまとめて返す。
 */
function keiba_race_sync_get_races_by_track($ymd)
{
    $query = new WP_Query(array(
        'post_type'      => 'race',
        'post_status'    => 'publish',
        'posts_per_page' => 200, // 1日の全開催分。中央+地方で最大でも100前後。
        'no_found_rows'  => true,
        'meta_query'     => array(
            array(
                'key'     => 'race_key',
                'value'   => $ymd . '-',
                'compare' => 'LIKE',
            ),
        ),
    ));

    $tracks = array();
    foreach ($query->posts as $post) {
        $race_key = get_post_meta($post->ID, 'race_key', true);
        $parsed = keiba_race_sync_parse_race_key($race_key);
        if ($parsed === null || $parsed['date'] !== $ymd) {
            continue;
        }

        $code = $parsed['track'];
        if (!isset($tracks[$code])) {
            $tracks[$code] = array(
                'code'  => $code,
                'name'  => keiba_race_sync_track_name($code),
                'races' => array(),
            );
        }

        $tracks[$code]['races'][$parsed['number']] = array(
            'number'   => $parsed['number'],
            'race_key' => $race_key,
            'post_id'  => $post->ID,
            // 結果が入っていれば「結果」、まだなら「出走表」。ボタンの見た目を変える。
            'has_result' => keiba_race_sync_has_result($post->ID),
        );
    }

    // 競馬場コード順、レース番号順に整える。
    ksort($tracks, SORT_STRING);
    foreach ($tracks as &$track) {
        ksort($track['races'], SORT_NUMERIC);
    }
    unset($track);

    return $tracks;
}

function keiba_race_sync_has_result($post_id)
{
    $raw = get_post_meta($post_id, 'race_result', true);
    return !empty($raw) && trim($raw) !== '[]';
}

/**
 * ショートコード [keiba_race_selector]
 *
 *   date="today"       … 既定。サイトのタイムゾーンでの当日
 *   date="2026-08-03"  … 日付指定
 *
 * 出走表そのものは初期表示に含めず、レース選択時にRESTから取得して差し込む。
 * 1日分（最大100レース前後）のテーブルを全部埋め込むとページが重くなるため。
 */
add_shortcode('keiba_race_selector', function ($atts) {
    $atts = shortcode_atts(array('date' => 'today'), $atts, 'keiba_race_selector');

    $ymd = ($atts['date'] === 'today')
        ? wp_date('Ymd')
        : preg_replace('/[^0-9]/', '', $atts['date']);

    if (strlen($ymd) !== 8) {
        return '<p class="keiba-selector-empty">日付の指定が正しくありません。</p>';
    }

    $tracks = keiba_race_sync_get_races_by_track($ymd);

    keiba_race_sync_enqueue_selector_assets();

    $display_date = sprintf('%s年%s月%s日', substr($ymd, 0, 4), (int) substr($ymd, 4, 2), (int) substr($ymd, 6, 2));

    ob_start();

    if (empty($tracks)) {
        echo '<div class="keiba-selector">';
        echo '<p class="keiba-selector-empty">' . esc_html($display_date) . 'の開催情報はまだありません。</p>';
        echo '</div>';
        return ob_get_clean();
    }

    echo '<div class="keiba-selector" data-date="' . esc_attr($ymd) . '">';

    // STEP 1: 競馬場
    echo '<div class="keiba-step">';
    echo '<h3 class="keiba-step-title"><span class="keiba-step-badge">1</span>競馬場を選ぶ</h3>';
    echo '<div class="keiba-track-list">';
    foreach ($tracks as $track) {
        printf(
            '<button type="button" class="keiba-track-btn" data-track="%s">%s<small>%d R</small></button>',
            esc_attr($track['code']),
            esc_html($track['name']),
            count($track['races'])
        );
    }
    echo '</div></div>';

    // STEP 2: レース番号（競馬場ごとに用意し、選択された競馬場のぶんだけ表示する）
    echo '<div class="keiba-step keiba-step-races" hidden>';
    echo '<h3 class="keiba-step-title"><span class="keiba-step-badge">2</span>レースを選ぶ</h3>';
    foreach ($tracks as $track) {
        printf('<div class="keiba-race-grid" data-track="%s" hidden>', esc_attr($track['code']));
        foreach ($track['races'] as $race) {
            printf(
                '<button type="button" class="keiba-race-btn%s" data-race-key="%s">%dR%s</button>',
                $race['has_result'] ? ' is-finished' : '',
                esc_attr($race['race_key']),
                $race['number'],
                $race['has_result'] ? '<small>結果</small>' : ''
            );
        }
        echo '</div>';
    }
    echo '</div>';

    // STEP 3: 出走表／結果の表示先
    echo '<div class="keiba-step keiba-step-detail" hidden>';
    echo '<h3 class="keiba-step-title"><span class="keiba-step-badge">3</span><span class="keiba-detail-heading">出走表</span></h3>';
    echo '<div class="keiba-detail-body" aria-live="polite"></div>';
    echo '</div>';

    echo '</div>';

    return ob_get_clean();
});

function keiba_race_sync_enqueue_selector_assets()
{
    wp_enqueue_style(
        'keiba-race-sync',
        plugins_url('assets/keiba-race-sync.css', __FILE__),
        array(),
        KEIBA_RACE_SYNC_ASSET_VER
    );
    wp_enqueue_script(
        'keiba-race-selector',
        plugins_url('assets/keiba-race-selector.js', __FILE__),
        array(),
        KEIBA_RACE_SYNC_ASSET_VER,
        true
    );
    wp_localize_script('keiba-race-selector', 'keibaRaceSelector', array(
        'endpoint' => rest_url('keiba-race-sync/v1/race'),
    ));
}

/**
 * レース選択時に呼ばれる読み取り専用エンドポイント。
 * 表示用HTMLはPHP側の描画関数をそのまま使い、JS側にテーブル生成を二重実装しない。
 */
add_action('rest_api_init', function () {
    register_rest_route('keiba-race-sync/v1', '/race', array(
        'methods'             => 'GET',
        'permission_callback' => '__return_true', // 公開ページの表示用（読み取りのみ）
        'args'                => array(
            'race_key' => array(
                'required'          => true,
                'sanitize_callback' => 'sanitize_text_field',
            ),
        ),
        'callback' => function ($request) {
            $race_key = $request->get_param('race_key');
            if (keiba_race_sync_parse_race_key($race_key) === null) {
                return new WP_Error('keiba_bad_race_key', 'race_key の形式が不正です。', array('status' => 400));
            }

            $posts = get_posts(array(
                'post_type'      => 'race',
                'post_status'    => 'publish',
                'posts_per_page' => 1,
                'no_found_rows'  => true,
                'meta_query'     => array(
                    array('key' => 'race_key', 'value' => $race_key),
                ),
            ));

            if (empty($posts)) {
                return new WP_Error('keiba_race_not_found', 'レースが見つかりません。', array('status' => 404));
            }

            $post = $posts[0];
            return array(
                'race_key'   => $race_key,
                'title'      => get_the_title($post),
                'permalink'  => get_permalink($post),
                'has_result' => keiba_race_sync_has_result($post->ID),
                'html'       => keiba_race_sync_render_race($post->ID),
            );
        },
    ));
});

/**
 * レース投稿のタイトルに含まれる競馬場コードを名称に置き換える。
 *
 * タイトルは収集アプリ側が「2026/08/04 30 12R 出走表」の形式で作るため、
 * そのままだと利用者に競馬場コードが見えてしまう（選択UIでは「門別」と出るのに
 * 見出しだけ「30」になり不揃いになる）。
 *
 * 保存されているタイトルを書き換えるのではなく表示時に変換するため、
 * 既存の投稿にも遡って効き、収集アプリの再実行は不要。
 */
add_filter('the_title', function ($title, $post_id = null) {
    if ($post_id === null || get_post_type($post_id) !== 'race') {
        return $title;
    }

    // 「日付 競馬場コード レース番号R 種別」の形のときだけ置き換える。
    return preg_replace_callback(
        '#^(\d{4}/\d{2}/\d{2})\s+([0-9A-Za-z]{2})\s+(\d+R\b)#u',
        function ($m) {
            return $m[1] . ' ' . keiba_race_sync_track_name($m[2]) . ' ' . $m[3];
        },
        $title
    );
}, 10, 2);

/**
 * 固定ページのスラッグが "race" だと、カスタム投稿タイプ race のアーカイブURLと
 * 衝突してページを開けなくなる（アーカイブ側が優先される）。
 * 実際に起きた事象のため、管理画面で気づけるようにしておく。
 */
add_action('admin_notices', function () {
    $conflict = get_page_by_path('race', OBJECT, 'page');
    if (!$conflict) {
        return;
    }

    echo '<div class="notice notice-warning"><p><strong>Keiba Race Sync:</strong> ';
    echo '固定ページ「' . esc_html(get_the_title($conflict)) . '」のスラッグが <code>race</code> のため、';
    echo 'レース一覧アーカイブ（<code>/race/</code>）と衝突しページを表示できません。';
    echo '<a href="' . esc_url(get_edit_post_link($conflict)) . '">スラッグを変更</a>してください';
    echo '（例: <code>today-races</code>）。';
    echo '</p></div>';
});

/* ==========================================================================
 * 予想印の入力欄（管理画面）
 *
 * 予想はデータ提供元に無いため、サイト運営側で入力する。カスタムフィールドを
 * 直接編集させずに済むよう、出走馬を並べた専用の入力欄を用意する。
 * 収集アプリは predictions を送信しないため、自動更新で消えることはない。
 * ========================================================================== */

add_action('add_meta_boxes', function () {
    add_meta_box(
        'keiba-race-predictions',
        '予想印',
        'keiba_race_sync_render_predictions_metabox',
        'race',
        'normal',
        'high'
    );
});

function keiba_race_sync_render_predictions_metabox($post)
{
    $entries = keiba_race_sync_decode_meta($post->ID, 'race_card');
    if (empty($entries)) {
        // 結果だけ入っている場合は結果側から馬を拾う。
        $entries = keiba_race_sync_decode_meta($post->ID, 'race_result');
    }

    if (empty($entries)) {
        echo '<p>出走表がまだ取り込まれていません。取り込み後に入力できます。</p>';
        return;
    }

    $predictions = keiba_race_sync_decode_meta($post->ID, 'predictions');
    $marks = array('', '◎', '○', '▲', '△', '☆', '×');

    wp_nonce_field('keiba_race_predictions_save', 'keiba_race_predictions_nonce');

    echo '<p>印を付けた馬だけ選択してください。1頭も選ばなければ「予想」列は表示されません。</p>';
    echo '<table class="widefat striped" style="max-width:640px">';
    echo '<thead><tr><th style="width:5em">馬番</th><th>馬名</th><th style="width:8em">予想印</th></tr></thead><tbody>';

    foreach ($entries as $e) {
        $umaban = $e['umaban'] ?? '';
        if ($umaban === '') {
            continue;
        }
        $current = keiba_race_sync_prediction_for($predictions, $umaban);

        echo '<tr>';
        echo '<td>' . esc_html($umaban) . '</td>';
        echo '<td>' . esc_html($e['horseName'] ?? '') . '</td>';
        echo '<td><select name="keiba_predictions[' . esc_attr($umaban) . ']">';
        foreach ($marks as $mark) {
            printf(
                '<option value="%s"%s>%s</option>',
                esc_attr($mark),
                selected($current, $mark, false),
                $mark === '' ? '—' : esc_html($mark)
            );
        }
        echo '</select></td>';
        echo '</tr>';
    }

    echo '</tbody></table>';
}

add_action('save_post_race', function ($post_id) {
    if (defined('DOING_AUTOSAVE') && DOING_AUTOSAVE) {
        return;
    }
    if (!isset($_POST['keiba_race_predictions_nonce'])
        || !wp_verify_nonce(sanitize_text_field(wp_unslash($_POST['keiba_race_predictions_nonce'])), 'keiba_race_predictions_save')) {
        return;
    }
    if (!current_user_can('edit_post', $post_id)) {
        return;
    }

    $submitted = isset($_POST['keiba_predictions']) && is_array($_POST['keiba_predictions'])
        ? wp_unslash($_POST['keiba_predictions'])
        : array();

    $clean = array();
    foreach ($submitted as $umaban => $mark) {
        $mark = sanitize_text_field($mark);
        if ($mark === '') {
            continue; // 印なしは保存しない（空の列が出るのを防ぐ）
        }
        $clean[(string) (int) $umaban] = $mark;
    }

    if (empty($clean)) {
        delete_post_meta($post_id, 'predictions');
    } else {
        update_post_meta($post_id, 'predictions', wp_json_encode($clean, JSON_UNESCAPED_UNICODE));
    }
});
