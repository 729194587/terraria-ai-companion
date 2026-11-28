-- Movement, observations and stopping are owned by the shared directional explorer.
return function(direction, seek)
    assert(direction == "left" or direction == "right" or direction == "up" or direction == "down",
           "explore expects left, right, up or down")
    assert(seek == nil or type(seek) == "string", "expected optional resource or passage")
    return api("_directional_explore", {direction=direction, seek=seek})
end
