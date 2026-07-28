<?php
/**
 * Plugin Name: Keiba Race Sync
 * Description: JV-Link/UmaConn連携の常駐アプリ（KeibaDataCollector）から送られる出走表・結果データを受け取り、
 *              カスタム投稿タイプ「race」として保存・表示する。
 * Version: 0.1.0
 */

if (!defined('ABSPATH')) {
    exit;
}

define('KEIBA_RACE_SYNC_JSON_META_KEYS', array('race_card', 'race_result', 'payouts', 'corner_passage'));

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
        'supports' => array('title'),
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
            '0.1.0'
        );
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

    ob_start();
    echo '<div class="keiba-race">';

    if (!empty($race_result)) {
        keiba_race_sync_render_result_table($race_result);
    } elseif (!empty($race_card)) {
        keiba_race_sync_render_card_table($race_card);
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

function keiba_race_sync_render_card_table($entries)
{
    echo '<table class="keiba-table keiba-card-table">';
    echo '<thead><tr>'
        . '<th>枠</th><th>馬番</th><th>馬名</th><th>性齢</th><th>斤量</th><th>騎手</th><th>厩舎</th>'
        . '</tr></thead><tbody>';
    foreach ($entries as $e) {
        echo '<tr>';
        echo '<td>' . keiba_race_sync_waku_badge($e['waku'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['umaban'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['horseName'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['sexAge'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['kinryo'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['jockeyName'] ?? '') . '</td>';
        echo '<td>' . esc_html($e['trainerName'] ?? '') . '</td>';
        echo '</tr>';
    }
    echo '</tbody></table>';
}

function keiba_race_sync_render_result_table($entries)
{
    echo '<table class="keiba-table keiba-result-table">';
    echo '<thead><tr>'
        . '<th>着順</th><th>枠</th><th>馬番</th><th>馬名</th><th>性齢</th><th>斤量</th><th>騎手</th>'
        . '<th>タイム</th><th>着差</th><th>人気</th><th>単勝</th><th>後3F</th><th>厩舎</th><th>馬体重(増減)</th>'
        . '</tr></thead><tbody>';
    foreach ($entries as $e) {
        $bataiju = esc_html($e['bataijuuZengo'] ?? '');
        $zogen = isset($e['bataijuuZogen']) ? (int) $e['bataijuuZogen'] : 0;
        $zogen_text = $zogen > 0 ? "(+{$zogen})" : ($zogen < 0 ? "({$zogen})" : '(0)');

        echo '<tr>';
        echo '<td>' . esc_html($e['chakujun'] ?? '') . '</td>';
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
        echo '</tr>';
    }
    echo '</tbody></table>';
}

function keiba_race_sync_render_payout_table($payouts)
{
    echo '<table class="keiba-table keiba-payout-table">';
    echo '<caption>払戻金</caption>';
    echo '<thead><tr><th>券種</th><th>組み合わせ</th><th>金額</th><th>人気</th></tr></thead><tbody>';
    foreach ($payouts as $p) {
        echo '<tr>';
        echo '<td>' . esc_html($p['ticketType'] ?? '') . '</td>';
        echo '<td>' . esc_html($p['combination'] ?? '') . '</td>';
        echo '<td>' . esc_html(number_format((float) ($p['amount'] ?? 0))) . '円</td>';
        echo '<td>' . esc_html($p['ninki'] ?? '') . '人気</td>';
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
