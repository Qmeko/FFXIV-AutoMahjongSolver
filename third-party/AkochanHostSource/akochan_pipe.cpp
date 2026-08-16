#include "share/include.hpp"
#include "share/types.hpp"
#include "ai_src/selector.hpp"

int main(int argc, char** argv)
{
    if (argc != 3)
    {
        std::cerr << "usage: akochan_pipe.exe <setup_mjai.json> <player_id>" << std::endl;
        return 2;
    }

    const json11::Json tactics = load_json_from_file(argv[1]);
    set_tactics_one(tactics);

    const int player_id = std::atoi(argv[2]);
    if (player_id < 0 || player_id > 3)
    {
        std::cerr << "player_id must be 0..3" << std::endl;
        return 2;
    }

    Moves game_record;
    std::string line;
    std::string error;
    while (std::getline(std::cin, line))
    {
        json11::Json received = json11::Json::parse(line, error);
        if (!error.empty())
        {
            std::cerr << "invalid JSON: " << error << std::endl;
            error.clear();
            continue;
        }

        const std::string type = received["type"].string_value();
        if (type == "error")
            continue;

        // Mortal accepts the standard mjai start_game emitted by the plugin.
        // Akochan's selector additionally requires these two legacy fields.
        if (type == "start_game"
            && (received["kyoku_first"].is_null() || received["aka_flag"].is_null()))
        {
            json11::Json::object normalized = received.object_items();
            if (received["kyoku_first"].is_null())
                normalized["kyoku_first"] = 4;
            if (received["aka_flag"].is_null())
                normalized["aka_flag"] = true;
            received = json11::Json(normalized);
        }

        if (type == "start_kyoku" && !game_record.empty())
            game_record = Moves(game_record.begin(), game_record.begin() + 1);

        game_record.push_back(received);
        if (!received["can_act"].is_null() && !received["can_act"].bool_value())
            continue;
        if (received["actor"].is_null())
            continue;

        const int actor = received["actor"].int_value();
        // The plugin marks only the final event of a decision batch with
        // can_act=true.  After our own Chi or Pon, mjai requires an immediate
        // discard without a following tsumo event, so those two completed meld
        // events must invoke the selector just like our own tsumo.
        const bool actionable =
            actor == player_id
            || (actor != player_id && (type == "dahai" || type == "kakan"));
        if (!actionable)
            continue;

        Moves best_moves = ai(game_record, player_id, false);
        std::cout << json11::Json(best_moves).dump() << std::endl;
    }

    return 0;
}
