class EventsMixin:
    """
    Legacy event helpers.
    NOTE: Main window now owns eventFilter/mousePressEvent directly to avoid
    unreachable MRO callback paths and duplicate event handling.
    """
    pass

    